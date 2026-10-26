using System.Text.Json;
using Akasha.Core.Data;
using Akasha.Core.Models;
using Akasha.Core.Query;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Core.Services;

/// <summary>
/// Жизненный цикл proposals: агент создаёт proposal (pending), владелец approve → применяется,
/// reject с комментарием (обязателен). base_version — оптимистичная блокировка (409 при конфликте).
/// </summary>
public class ProposalService
{
    private readonly AkashaDbContext _db;
    private readonly AuditService _audit;
    private readonly IEmbeddingService _embeddings;
    private readonly EmbeddingQueue? _queue;

    public ProposalService(AkashaDbContext db, AuditService audit, IEmbeddingService embeddings, EmbeddingQueue? queue = null)
    {
        _db = db; _audit = audit; _embeddings = embeddings; _queue = queue;
    }

    public sealed class ValidationException : Exception
    {
        public ValidationException(string m) : base(m) { }
    }
    public sealed class ConflictException : Exception
    {
        public ConflictException(string m) : base(m) { }
    }

    // ─── создание (агент) ─────────────────────────────────────────────────────

    public async Task<Proposal> CreateAsync(Guid? agentId, string op, string target,
        Guid? targetId, JsonElement payload, int? baseVersion, CancellationToken ct = default)
    {
        if (!ProposalOp.All.Contains(op))
            throw new ValidationException($"op must be one of: {string.Join(", ", ProposalOp.All)}");
        if (!ProposalTarget.All.Contains(target))
            throw new ValidationException($"target must be one of: {string.Join(", ", ProposalTarget.All)}");
        if (payload.ValueKind != JsonValueKind.Object)
            throw new ValidationException("payload must be a JSON object.");

        // Валидация payload по типу цели
        switch (target)
        {
            case ProposalTarget.Entries:
                ValidateEntryPayload(payload, op);
                break;
            case ProposalTarget.Projects:
                if (op is ProposalOp.Create)  // для update name опционален — меняем только переданные поля
                {
                    if (!payload.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(n.GetString()))
                        throw new ValidationException("projects payload requires non-empty 'name'.");
                }
                break;
            case ProposalTarget.Scripts:
                if (op is ProposalOp.Create or ProposalOp.Update)
                {
                    ValidateScriptPayload(payload);
                }
                break;
            case ProposalTarget.EntryLinks:
                if (!payload.TryGetProperty("from_id", out _) || !payload.TryGetProperty("to_id", out _))
                    throw new ValidationException("entry_links payload requires from_id and to_id.");
                break;
        }

        if (op is ProposalOp.Update or ProposalOp.Delete)
        {
            if (targetId is null) throw new ValidationException($"{op} requires target_id.");
            await CheckBaseVersionAsync(target, targetId.Value, baseVersion, ct);
        }

        var proposal = new Proposal
        {
            AgentId = agentId,
            Op = op,
            Target = target,
            TargetId = targetId,
            Payload = payload.Clone(),
            BaseVersion = baseVersion,
            Status = ProposalStatus.Pending
        };
        _db.Proposals.Add(proposal);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(agentId?.ToString() ?? "owner", "propose", target, proposal.Id,
            JsonSerializer.Serialize(new { op, target, target_id = targetId }), ct);
        return proposal;
    }

    private void ValidateEntryPayload(JsonElement payload, string op)
    {
        if (op == ProposalOp.Delete) return;
        if (op == ProposalOp.Create)
        {
            if (!payload.TryGetProperty("title", out var t) || t.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(t.GetString()))
                throw new ValidationException("entries create payload requires 'title'.");
            if (!payload.TryGetProperty("body", out var b) || b.ValueKind != JsonValueKind.String)
                throw new ValidationException("entries create payload requires 'body'.");
            if (payload.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String &&
                k.GetString() is { } kind && !EntryKinds.All.Contains(kind))
                throw new ValidationException($"kind must be one of: {string.Join(", ", EntryKinds.All)}");
        }
    }

    private void ValidateScriptPayload(JsonElement payload)
    {
        if (!payload.TryGetProperty("slug", out var s) || s.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(s.GetString()))
            throw new ValidationException("scripts payload requires 'slug'.");
        if (!payload.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String)
            throw new ValidationException("scripts payload requires 'name'.");
        if (!payload.TryGetProperty("query", out var q) || q.ValueKind != JsonValueKind.Object)
            throw new ValidationException("scripts payload requires object 'query' (structured JSON Query).");
        if (payload.TryGetProperty("output_format", out var of) && of.ValueKind == JsonValueKind.String &&
            of.GetString() is { } fmt && !OutputFormats.All.Contains(fmt))
            throw new ValidationException($"output_format must be one of: {string.Join(", ", OutputFormats.All)}");
        // dry-validate query через engine (без выполнения против данных — только structural check 'from')
        if (!q.TryGetProperty("from", out var from) || from.ValueKind != JsonValueKind.String ||
            from.GetString() is not { } tbl || tbl is not ("entries" or "projects" or "entry_links"))
            throw new ValidationException("script query.from must be entries|projects|entry_links.");
    }

    private async Task CheckBaseVersionAsync(string target, Guid targetId, int? baseVersion, CancellationToken ct)
    {
        if (baseVersion is null) return;
        int current = target switch
        {
            ProposalTarget.Entries => await _db.Entries.Where(e => e.Id == targetId).Select(e => e.Version).FirstOrDefaultAsync(ct),
            ProposalTarget.Scripts => await _db.Scripts.Where(s => s.Id == targetId).Select(s => s.Version).FirstOrDefaultAsync(ct),
            _ => baseVersion.Value // projects/links: version-контроль не ведём
        };
        if (target is ProposalTarget.Entries or ProposalTarget.Scripts && current != baseVersion.Value)
            throw new ConflictException($"base_version mismatch: current={current}, proposed={baseVersion}.");
    }

    // ─── review (владелец) ────────────────────────────────────────────────────

    public async Task<Proposal> ApproveAsync(Guid proposalId, CancellationToken ct = default)
    {
        var p = await _db.Proposals.FirstOrDefaultAsync(x => x.Id == proposalId, ct)
                ?? throw new KeyNotFoundException("proposal not found");
        if (p.Status != ProposalStatus.Pending)
            throw new ValidationException($"proposal status is '{p.Status}', only pending can be approved.");

        await ApplyAsync(p, ct);

        p.Status = ProposalStatus.Applied;
        p.ReviewedAt = p.AppliedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("owner", "approve", p.Target, p.Id, null, ct);
        return p;
    }

    public async Task<Proposal> RejectAsync(Guid proposalId, string comment, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(comment))
            throw new ValidationException("reject requires a comment.");
        var p = await _db.Proposals.FirstOrDefaultAsync(x => x.Id == proposalId, ct)
                ?? throw new KeyNotFoundException("proposal not found");
        if (p.Status != ProposalStatus.Pending)
            throw new ValidationException($"proposal status is '{p.Status}', only pending can be rejected.");

        p.Status = ProposalStatus.Rejected;
        p.ReviewComment = comment;
        p.ReviewedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync("owner", "reject", p.Target, p.Id,
            JsonSerializer.Serialize(new { comment }), ct);
        return p;
    }

    /// <summary>Применение содержимого proposal к данным (без смены статуса).</summary>
    private async Task ApplyAsync(Proposal p, CancellationToken ct)
    {
        switch (p.Target)
        {
            case ProposalTarget.Entries:
                await ApplyEntryAsync(p, ct);
                break;
            case ProposalTarget.Projects:
                await ApplyProjectAsync(p, ct);
                break;
            case ProposalTarget.Scripts:
                await ApplyScriptAsync(p, ct);
                break;
            case ProposalTarget.EntryLinks:
                await ApplyLinkAsync(p, ct);
                break;
        }
    }

    private async Task ApplyEntryAsync(Proposal p, CancellationToken ct)
    {
        switch (p.Op)
        {
            case ProposalOp.Create:
            {
                var e = MapEntry(p.Payload);
                // slug проекта резолвим в ProjectId (MapEntry понимает только project_id как Guid)
                if (e.ProjectId is null && GetStr(p.Payload, "project") is { } projSlug)
                    e.ProjectId = await ResolveProjectId(projSlug, ct)
                        ?? throw new ValidationException($"unknown project '{projSlug}'.");
                e.CreatedBy = p.AgentId?.ToString();
                e.Embedding = _embeddings.Embed(e.Title + "\n" + e.Body);
                e.StateHash = ContentHash.Of(e);
                _db.Entries.Add(e);
                p.Payload = Normalize(JsonSerializer.SerializeToElement(new { id = e.Id }));
                break;
            }
            case ProposalOp.Update:
            {
                if (p.TargetId is not { } id) throw new ValidationException("update requires target_id.");
                var e = await _db.Entries.FirstOrDefaultAsync(x => x.Id == id, ct)
                        ?? throw new ValidationException("target entry not found.");
                if (p.BaseVersion is { } bv && e.Version != bv)
                    throw new ConflictException($"entry changed since proposal (v{e.Version} != v{bv}).");
                var upd = p.Payload;
                if (upd.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) e.Title = t.GetString()!;
                if (upd.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String) e.Body = b.GetString()!;
                if (upd.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String) e.Kind = k.GetString()!;
                if (upd.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array)
                    e.Tags = tg.EnumerateArray().Select(x => x.GetString()!).Where(s => s is not null).ToList();
                if (upd.TryGetProperty("source_url", out var su) && su.ValueKind == JsonValueKind.String)
                    e.SourceUrl = su.GetString();
                if (upd.TryGetProperty("project", out var pr) && pr.ValueKind == JsonValueKind.String)
                    e.ProjectId = await ResolveProjectId(pr.GetString(), ct);
                else if (upd.TryGetProperty("project_id", out var pid) && pid.ValueKind == JsonValueKind.String &&
                         Guid.TryParse(pid.GetString(), out var pg))
                    e.ProjectId = pg;
                e.Version += 1;
                e.UpdatedAt = DateTimeOffset.UtcNow;
                e.Embedding = _embeddings.Embed(e.Title + "\n" + e.Body);
                e.StateHash = ContentHash.Of(e);
                break;
            }
            case ProposalOp.Delete:
            {
                if (p.TargetId is not { } id) throw new ValidationException("delete requires target_id.");
                var e = await _db.Entries.FirstOrDefaultAsync(x => x.Id == id, ct);
                if (e is not null)
                {
                    e.DeletedAt = DateTimeOffset.UtcNow;
                    e.Version += 1;
                    e.UpdatedAt = DateTimeOffset.UtcNow;
                }
                break;
            }
        }
    }

    private async Task ApplyProjectAsync(Proposal p, CancellationToken ct)
    {
        switch (p.Op)
        {
            case ProposalOp.Create:
            {
                var proj = new Project
                {
                    Slug = Slugify(GetStr(p.Payload, "slug") ?? GetStr(p.Payload, "name") ?? "untitled"),
                    Name = GetStr(p.Payload, "name") ?? "Untitled",
                    Description = GetStr(p.Payload, "description"),
                    Status = GetStr(p.Payload, "status") ?? ProjectStatus.Active,
                };
                if (p.Payload.TryGetProperty("stack", out var st) && st.ValueKind == JsonValueKind.Array)
                    proj.Stack = st.EnumerateArray().Select(x => x.GetString()!).Where(s => s is not null).ToList();
                if (p.Payload.TryGetProperty("links", out var ln) && ln.ValueKind == JsonValueKind.Object)
                    proj.Links = ln.Clone();
                _db.Projects.Add(proj);
                p.Payload = Normalize(JsonSerializer.SerializeToElement(new { id = proj.Id, slug = proj.Slug }));
                break;
            }
            case ProposalOp.Update:
            {
                var proj = await FindProjectAsync(p, ct);
                if (proj is null) throw new ValidationException("target project not found.");
                if (p.Payload.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) proj.Name = n.GetString()!;
                if (p.Payload.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) proj.Description = d.GetString();
                if (p.Payload.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String) proj.Status = s.GetString()!;
                proj.UpdatedAt = DateTimeOffset.UtcNow;
                break;
            }
            case ProposalOp.Delete:
            {
                var proj = await FindProjectAsync(p, ct);
                if (proj is not null) { proj.DeletedAt = DateTimeOffset.UtcNow; proj.UpdatedAt = DateTimeOffset.UtcNow; }
                break;
            }
        }
    }

    private async Task<Project?> FindProjectAsync(Proposal p, CancellationToken ct)
    {
        if (p.TargetId is { } id) return await _db.Projects.FirstOrDefaultAsync(x => x.Id == id, ct);
        var slug = GetStr(p.Payload, "slug");
        if (slug is not null) return await _db.Projects.FirstOrDefaultAsync(x => x.Slug == slug, ct);
        return null;
    }

    private async Task ApplyScriptAsync(Proposal p, CancellationToken ct)
    {
        switch (p.Op)
        {
            case ProposalOp.Create:
            {
                var sc = MapScript(p.Payload, p);
                _db.Scripts.Add(sc);
                p.Payload = Normalize(JsonSerializer.SerializeToElement(new { id = sc.Id, slug = sc.Slug }));
                break;
            }
            case ProposalOp.Update:
            {
                var sc = p.TargetId is { } sid
                    ? await _db.Scripts.FirstOrDefaultAsync(x => x.Id == sid, ct)
                    : await _db.Scripts.FirstOrDefaultAsync(x => x.Slug == GetStr(p.Payload, "slug"), ct);
                if (sc is null) throw new ValidationException("target script not found.");
                var fresh = MapScript(p.Payload, p);
                sc.Name = fresh.Name;
                sc.Description = fresh.Description;
                sc.Query = fresh.Query;
                sc.Transform = fresh.Transform;
                sc.OutputFormat = fresh.OutputFormat;
                sc.ParamsSchema = fresh.ParamsSchema;
                sc.CacheTtl = fresh.CacheTtl;
                sc.Version += 1;
                sc.UpdatedAt = DateTimeOffset.UtcNow;
                break;
            }
            case ProposalOp.Delete:
            {
                var sc = p.TargetId is { } sid
                    ? await _db.Scripts.FirstOrDefaultAsync(x => x.Id == sid, ct)
                    : await _db.Scripts.FirstOrDefaultAsync(x => x.Slug == GetStr(p.Payload, "slug"), ct);
                if (sc is not null) _db.Scripts.Remove(sc);
                break;
            }
        }
    }

    private static Script MapScript(JsonElement payload, Proposal p)
    {
        var sc = new Script
        {
            Slug = Slugify(GetStr(payload, "slug") ?? throw new ValidationException("slug required")),
            Name = GetStr(payload, "name") ?? "Untitled script",
            Description = GetStr(payload, "description"),
            CreatedBy = p.AgentId?.ToString(),
            ApprovedProposalId = p.Id,
        };
        sc.Query = payload.GetProperty("query").Clone();
        if (payload.TryGetProperty("transform", out var tr) && tr.ValueKind == JsonValueKind.String)
            sc.Transform = tr.GetString();
        if (payload.TryGetProperty("output_format", out var of) && of.ValueKind == JsonValueKind.String)
            sc.OutputFormat = of.GetString()!;
        if (payload.TryGetProperty("params_schema", out var ps) && ps.ValueKind == JsonValueKind.Object)
            sc.ParamsSchema = ps.Clone();
        if (payload.TryGetProperty("cache_ttl", out var ttl) && ttl.TryGetInt32(out var t))
            sc.CacheTtl = t;
        return sc;
    }

    private async Task ApplyLinkAsync(Proposal p, CancellationToken ct)
    {
        if (p.Op == ProposalOp.Delete && p.Payload.TryGetProperty("from_id", out var fd) &&
            p.Payload.TryGetProperty("to_id", out var td) &&
            Guid.TryParse(fd.GetString(), out var f) && Guid.TryParse(td.GetString(), out var t))
        {
            var rel = GetStr(p.Payload, "relation") ?? LinkRelations.Mentions;
            var link = await _db.EntryLinks.FindAsync(new object[] { f, t, rel }, ct);
            if (link is not null) _db.EntryLinks.Remove(link);
            return;
        }
        var pl = p.Payload;
        if (!pl.TryGetProperty("from_id", out var fromEl) || !Guid.TryParse(fromEl.GetString(), out var fromId))
            throw new ValidationException("entry_links payload requires valid from_id.");
        if (!pl.TryGetProperty("to_id", out var toEl) || !Guid.TryParse(toEl.GetString(), out var toId))
            throw new ValidationException("entry_links payload requires valid to_id.");
        var relation = GetStr(pl, "relation") ?? LinkRelations.Mentions;
        if (!LinkRelations.All.Contains(relation))
            throw new ValidationException($"relation must be one of: {string.Join(", ", LinkRelations.All)}");
        if (!_db.EntryLinks.Local.Any(l => l.FromId == fromId && l.ToId == toId && l.Relation == relation))
        {
            var existing = await _db.EntryLinks.FindAsync(new object[] { fromId, toId, relation }, ct);
            if (existing is null)
                _db.EntryLinks.Add(new EntryLink { FromId = fromId, ToId = toId, Relation = relation });
        }
    }

    private async Task<Guid?> ResolveProjectId(string? slug, CancellationToken ct)
    {
        if (slug is null) return null;
        var p = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == slug, ct);
        return p?.Id;
    }

    internal static Entry MapEntry(JsonElement payload)
    {
        var e = new Entry
        {
            Title = GetStr(payload, "title")!,
            Body = GetStr(payload, "body") ?? "",
            Kind = GetStr(payload, "kind") ?? EntryKinds.Note,
            SourceUrl = GetStr(payload, "source_url"),
        };
        if (payload.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array)
            e.Tags = tg.EnumerateArray().Select(x => x.GetString()!).Where(s => s is not null).ToList();
        if (payload.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object)
            e.Metadata = md.Clone();
        if (payload.TryGetProperty("id", out var idEl) && Guid.TryParse(idEl.GetString(), out var fixedId))
            e.Id = fixedId;
        var projectIdStr = GetStr(payload, "project_id");
        if (projectIdStr is not null && Guid.TryParse(projectIdStr, out var pid)) e.ProjectId = pid;
        return e;
    }

    private static JsonElement Normalize(JsonElement el) => el.Clone();

    private static string? GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string Slugify(string input)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in input.ToLowerInvariant().Trim())
        {
            if (char.IsLetterOrDigit(ch) && ch < 128) sb.Append(ch);
            else if (ch is '-' or '_' or ' ' or '\t') { if (sb.Length > 0 && sb[^1] != '-') sb.Append('-'); }
        }
        var slug = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(slug) ? "untitled" : slug;
    }
}
