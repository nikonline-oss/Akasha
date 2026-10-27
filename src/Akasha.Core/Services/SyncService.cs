using System.Text.Json;
using Akasha.Core.Data;
using Akasha.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Core.Services;

/// <summary>
/// Diff-based синхронизация portable ↔ server (§9). Source of truth — сервер.
/// Синхронизируются entries/projects/entry_links/scripts/proposals;
/// secrets, audit_log, agents, owners — физически исключены на уровне сервиса.
/// v1: last-write-wins по updated_at + state_hash для детекта конфликтов (conflict → proposal).
/// </summary>
public class SyncService
{
    private readonly AkashaDbContext _db;
    private readonly AuditService _audit;
    private readonly IEmbeddingService _embeddings;

    public SyncService(AkashaDbContext db, AuditService audit, IEmbeddingService embeddings)
    {
        _db = db; _audit = audit; _embeddings = embeddings;
    }

    /// <summary>Кэш slug→id проектов в рамках экземпляра сервиса (scoped per request).</summary>
    private readonly Dictionary<string, Guid?> _slugCache = new();

    /// <summary>Сущности, которые никогда не отдаются и не принимаются sync-эндпоинтом.</summary>
    public static readonly string[] ForbiddenEntities = { "secrets", "audit_log", "agents", "owners" };

    public record SyncChange(string Entity, string Op, JsonElement Data, DateTimeOffset UpdatedAt);

    public sealed class SyncRejectException : Exception
    {
        public SyncRejectException(string m) : base(m) { }
    }

    /// <summary>Применение pull-изменений на стороне portable-клиента (last-write-wins по updated_at).</summary>
    public async Task<int> ApplyPulledChangesAsync(List<SyncChange> changes, CancellationToken ct = default)
    {
        int applied = 0;
        foreach (var ch in changes)
        {
            if (ForbiddenEntities.Contains(ch.Entity))
                throw new SyncRejectException($"entity '{ch.Entity}' is never synchronized");
            switch (ch.Entity)
            {
                case "entries":
                {
                    var (a, _, _) = await PushEntry(ch, "pull", ct);
                    applied += a;
                    break;
                }
                case "projects":
                    if (await PushProject(ch, ct)) applied++;
                    break;
                case "entry_links":
                    if (await PushLink(ch, ct)) applied++;
                    break;
                case "scripts":
                    if (await PushScript(ch, ct)) applied++;
                    break;
                default:
                    throw new SyncRejectException($"unknown entity '{ch.Entity}'");
            }
        }
        await _db.SaveChangesAsync(ct);
        return applied;
    }

    // ─── PULL: всё, что изменилось после `since` ──────────────────────────────

    public async Task<List<SyncChange>> PullAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var changes = new List<SyncChange>();

        // SQLite хранит DateTimeOffset как TEXT с квантизацией до миллисекунд;
        // сравнение "впритык" (same tick) может потерять запись → берём буфер 2 сек.
        var threshold = since.AddSeconds(-2);

        foreach (var e in await _db.Entries.Where(e => e.UpdatedAt > threshold).ToListAsync(ct))
            changes.Add(new SyncChange("entries", e.DeletedAt is not null ? "delete" : "upsert",
                SerializeEntry(e), e.UpdatedAt));

        foreach (var p in await _db.Projects.Where(p => p.UpdatedAt > threshold).ToListAsync(ct))
            changes.Add(new SyncChange("projects", p.DeletedAt is not null ? "delete" : "upsert",
                JsonSerializer.SerializeToElement(new
                {
                    id = p.Id, slug = p.Slug, name = p.Name, description = p.Description,
                    status = p.Status, stack = p.Stack,
                    links = p.Links?.GetRawText(), created_at = p.CreatedAt,
                    updated_at = p.UpdatedAt, deleted_at = p.DeletedAt
                }), p.UpdatedAt));

        foreach (var l in await _db.EntryLinks.Where(l => l.CreatedAt > threshold).ToListAsync(ct))
            changes.Add(new SyncChange("entry_links", "upsert",
                JsonSerializer.SerializeToElement(new { from_id = l.FromId, to_id = l.ToId, relation = l.Relation, created_at = l.CreatedAt }),
                l.CreatedAt));

        foreach (var s in await _db.Scripts.Where(s => s.UpdatedAt > threshold).ToListAsync(ct))
            changes.Add(new SyncChange("scripts", "upsert",
                JsonSerializer.SerializeToElement(new
                {
                    id = s.Id, slug = s.Slug, name = s.Name, description = s.Description,
                    query = s.Query, transform = s.Transform, output_format = s.OutputFormat,
                    params_schema = s.ParamsSchema, version = s.Version, cache_ttl = s.CacheTtl,
                    updated_at = s.UpdatedAt
                }), s.UpdatedAt));

        return changes.OrderByDescending(c => c.UpdatedAt).ToList();
    }

    // ─── PUSH: изменения с портативного клиента ───────────────────────────────

    public record PushResult(int Applied, int SkippedOlder, List<Guid> ConflictProposals);

    public async Task<PushResult> PushAsync(List<SyncChange> changes, string deviceId, CancellationToken ct = default)
    {
        int applied = 0, skipped = 0;
        var conflicts = new List<Guid>();

        foreach (var ch in changes)
        {
            if (ForbiddenEntities.Contains(ch.Entity))
                throw new SyncRejectException($"entity '{ch.Entity}' is never synchronized");

            switch (ch.Entity)
            {
                case "entries":
                {
                    var (a, s, conflictId) = await PushEntry(ch, deviceId, ct);
                    applied += a; skipped += s;
                    if (conflictId is { } cid) conflicts.Add(cid);
                    break;
                }
                case "projects":
                    if (await PushProject(ch, ct)) applied++; else skipped++;
                    break;
                case "entry_links":
                    if (await PushLink(ch, ct)) applied++; else skipped++;
                    break;
                case "scripts":
                    if (await PushScript(ch, ct)) applied++; else skipped++;
                    break;
                default:
                    throw new SyncRejectException($"unknown entity '{ch.Entity}'");
            }
        }

        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(deviceId, "sync", "push", null,
            JsonSerializer.Serialize(new { applied, skipped, conflicts = conflicts.Count }), ct);
        return new PushResult(applied, skipped, conflicts);
    }

    private async Task<(int applied, int skipped, Guid? conflict)> PushEntry(SyncChange ch, string deviceId, CancellationToken ct)
    {
        var data = ch.Data;
        if (!data.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var id))
            throw new SyncRejectException("entry change requires id");

        var local = await _db.Entries.Include(e => e.Project).FirstOrDefaultAsync(e => e.Id == id, ct);
        var remoteUpdated = ch.UpdatedAt;
        var remoteHash = data.TryGetProperty("state_hash", out var sh) && sh.ValueKind == JsonValueKind.String
            ? sh.GetString() : null;

        if (local is null)
        {
            var e = ProposalService.MapEntry(data);
            e.Id = id;
            if (data.TryGetProperty("created_at", out var ca) && ca.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(ca.GetString(), out var createdAt))
                e.CreatedAt = createdAt;
            e.CreatedBy = data.TryGetProperty("created_by", out var cb) && cb.ValueKind == JsonValueKind.String
                ? cb.GetString() : $"device:{deviceId}";
            e.UpdatedAt = remoteUpdated;
            e.Embedding = _embeddings.Embed(e.Title + "\n" + e.Body);
            e.StateHash = ContentHash.Of(e);
            if (data.TryGetProperty("deleted_at", out var del) && del.ValueKind != JsonValueKind.Null &&
                DateTimeOffset.TryParse(del.ToString(), out var dt))
                e.DeletedAt = dt;
            _db.Entries.Add(e);
            return (1, 0, null);
        }

        // last-write-wins по updated_at
        if (local.UpdatedAt >= remoteUpdated)
            return (0, 1, null);

        // Конфликт: обе стороны изменили одну запись (разные state_hash) → conflict-proposal,
        // а не молчаливая перезапись серверной версии (§9.3 ТЗ / sync-protocol.md §3).
        if (remoteHash is not null && local.StateHash is not null &&
            !string.Equals(remoteHash, local.StateHash, StringComparison.Ordinal))
        {
            var proposal = new Proposal
            {
                AgentId = null,
                Op = ProposalOp.Conflict,
                Target = ProposalTarget.Entries,
                TargetId = local.Id,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    server = new
                    {
                        id = local.Id, title = local.Title, body = local.Body, kind = local.Kind,
                        tags = local.Tags, updated_at = local.UpdatedAt, state_hash = local.StateHash,
                    },
                    portable = data,
                }),
                Status = ProposalStatus.Pending,
            };
            _db.Proposals.Add(proposal);
            await _audit.LogAsync(deviceId, "conflict", "entries", local.Id,
                JsonSerializer.Serialize(new { remote_hash = remoteHash, local_hash = local.StateHash }), ct);
            return (0, 0, proposal.Id);
        }

        ApplyEntryFields(local, data);
        local.UpdatedAt = remoteUpdated;
        local.Version += 1;
        local.Embedding = _embeddings.Embed(local.Title + "\n" + local.Body);
        local.StateHash = ContentHash.Of(local);
        if (ch.Op == "delete") local.DeletedAt = DateTimeOffset.UtcNow;
        else if (data.TryGetProperty("deleted_at", out var d2) && d2.ValueKind != JsonValueKind.Null)
            local.DeletedAt = DateTimeOffset.UtcNow;
        else local.DeletedAt = null;

        await _audit.LogAsync("sync", "update", "entries", local.Id,
            JsonSerializer.Serialize(new { source = "portable", last_write_wins = true }), ct);
        return (1, 0, null);
    }

    private void ApplyEntryFields(Entry e, JsonElement data)
    {
        if (data.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) e.Title = t.GetString()!;
        if (data.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String) e.Body = b.GetString()!;
        if (data.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String) e.Kind = k.GetString()!;
        if (data.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array)
            e.Tags = tg.EnumerateArray().Select(x => x.GetString()!).Where(s => s is not null).ToList();
        if (data.TryGetProperty("source_url", out var su) && su.ValueKind == JsonValueKind.String)
            e.SourceUrl = su.GetString();
        if (data.TryGetProperty("project_slug", out var ps) && ps.ValueKind == JsonValueKind.String)
        {
            var projId = ResolveProjectSlug(ps.GetString());
            if (projId is not null) e.ProjectId = projId;
        }
    }

    private Guid? ResolveProjectSlug(string? slug)
    {
        if (slug is null) return null;
        if (_slugCache.TryGetValue(slug, out var cached)) return cached;
        var id = _db.Projects.AsNoTracking()
            .Where(p => p.Slug == slug)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefault();
        _slugCache[slug] = id;
        return id;
    }

    private async Task<bool> PushProject(SyncChange ch, CancellationToken ct)
    {
        var data = ch.Data;
        var idStr = data.TryGetProperty("id", out var i) ? i.GetString() : null;
        var slug = data.TryGetProperty("slug", out var s) ? s.GetString() : null;
        Project? local = null;
        if (Guid.TryParse(idStr, out var id)) local = await _db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
        local ??= slug is not null ? await _db.Projects.FirstOrDefaultAsync(p => p.Slug == slug, ct) : null;

        if (local is null)
        {
            if (ch.Op == "delete") return false;
            local = new Project { Slug = slug ?? "imported", Name = data.TryGetProperty("name", out var n) ? n.GetString() ?? "Imported" : "Imported" };
            if (Guid.TryParse(idStr, out var nid)) local.Id = nid;
            _db.Projects.Add(local);
        }
        else if (local.UpdatedAt >= ch.UpdatedAt) return false;

        if (data.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String) local.Name = nm.GetString()!;
        if (data.TryGetProperty("description", out var de) && de.ValueKind == JsonValueKind.String) local.Description = de.GetString();
        if (data.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String) local.Status = st.GetString()!;
        local.UpdatedAt = ch.UpdatedAt;
        if (ch.Op == "delete") local.DeletedAt = DateTimeOffset.UtcNow;
        return true;
    }

    private async Task<bool> PushLink(SyncChange ch, CancellationToken ct)
    {
        var data = ch.Data;
        if (!data.TryGetProperty("from_id", out var fEl) || !Guid.TryParse(fEl.GetString(), out var fromId)) return false;
        if (!data.TryGetProperty("to_id", out var tEl) || !Guid.TryParse(tEl.GetString(), out var toId)) return false;
        var relation = data.TryGetProperty("relation", out var r) ? r.GetString() ?? LinkRelations.Mentions : LinkRelations.Mentions;
        var existing = await _db.EntryLinks.FindAsync(new object[] { fromId, toId, relation }, ct);
        if (ch.Op == "delete")
        {
            if (existing is not null) { _db.EntryLinks.Remove(existing); return true; }
            return false;
        }
        if (existing is null)
        {
            _db.EntryLinks.Add(new EntryLink { FromId = fromId, ToId = toId, Relation = relation });
            return true;
        }
        return false;
    }

    private async Task<bool> PushScript(SyncChange ch, CancellationToken ct)
    {
        var data = ch.Data;
        var slug = data.TryGetProperty("slug", out var s) ? s.GetString() : null;
        if (slug is null) return false;
        var local = await _db.Scripts.FirstOrDefaultAsync(x => x.Slug == slug, ct);
        if (local is null)
        {
            local = new Script { Slug = slug, Name = data.TryGetProperty("name", out var n) ? n.GetString() ?? slug : slug };
            _db.Scripts.Add(local);
        }
        else if (local.UpdatedAt >= ch.UpdatedAt) return false;

        if (data.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String) local.Name = nm.GetString()!;
        if (data.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.Object) local.Query = q.Clone();
        if (data.TryGetProperty("transform", out var tr) && tr.ValueKind == JsonValueKind.String) local.Transform = tr.GetString();
        if (data.TryGetProperty("output_format", out var of) && of.ValueKind == JsonValueKind.String) local.OutputFormat = of.GetString()!;
        if (data.TryGetProperty("cache_ttl", out var ttl) && ttl.TryGetInt32(out var t)) local.CacheTtl = t;
        local.Version += 1;
        local.UpdatedAt = ch.UpdatedAt;
        return true;
    }

    private static JsonElement SerializeEntry(Entry e) =>
        JsonSerializer.SerializeToElement(new
        {
            id = e.Id, project_id = e.ProjectId, kind = e.Kind, title = e.Title, body = e.Body,
            tags = e.Tags, source_url = e.SourceUrl, metadata = e.Metadata?.ToString(),
            version = e.Version, created_by = e.CreatedBy, created_at = e.CreatedAt,
            updated_at = e.UpdatedAt, deleted_at = e.DeletedAt, state_hash = e.StateHash
        });

}
