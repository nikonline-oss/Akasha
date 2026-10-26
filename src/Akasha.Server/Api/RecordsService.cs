using System.Text.Json;
using Akasha.Core.Data;
using Akasha.Core.Models;
using Akasha.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Server.Api;

/// <summary>
/// Реестр записей для UI: CRUD entries/projects напрямую владельцем (admin-зона).
/// Агенты этот сервис не вызывают — они только propose.
/// </summary>
public class RecordsService
{
    private readonly AkashaDbContext _db;
    private readonly AuditService _audit;
    private readonly IEmbeddingService _embeddings;
    private readonly EmbeddingQueue? _queue;

    public RecordsService(AkashaDbContext db, AuditService audit, IEmbeddingService embeddings, EmbeddingQueue? queue = null)
    {
        _db = db; _audit = audit; _embeddings = embeddings; _queue = queue;
    }

    // ─── Projects ─────────────────────────────────────────────────────────────

    public async Task<Project> CreateProjectAsync(string name, string? description, string? slug,
        List<string>? stack, string actor, CancellationToken ct = default)
    {
        slug = ProposalService.Slugify(slug ?? name);
        if (await _db.Projects.AnyAsync(p => p.Slug == slug && p.DeletedAt == null, ct))
            throw new InvalidOperationException($"project slug '{slug}' already exists");
        var p = new Project
        {
            Name = name, Slug = slug, Description = description,
            Stack = stack ?? new(),
        };
        _db.Projects.Add(p);
        await BumpDataVersion(ct);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(actor, "create", "projects", p.Id, null, ct);
        return p;
    }

    public async Task<Project> UpdateProjectAsync(Guid id, JsonElement patch, string actor, CancellationToken ct = default)
    {
        var p = await _db.Projects.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct)
                ?? throw new KeyNotFoundException("project not found");
        if (patch.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) p.Name = n.GetString()!;
        if (patch.TryGetProperty("description", out var d)) p.Description = d.ValueKind == JsonValueKind.Null ? null : d.GetString();
        if (patch.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
        {
            var v = s.GetString()!;
            if (!ProjectStatus.All.Contains(v)) throw new ArgumentException($"bad status '{v}'");
            p.Status = v;
        }
        if (patch.TryGetProperty("stack", out var st) && st.ValueKind == JsonValueKind.Array)
            p.Stack = st.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToList();
        if (patch.TryGetProperty("links", out var l))
            p.Links = l.ValueKind == JsonValueKind.Null ? null : l.Clone();
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await BumpDataVersion(ct);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(actor, "update", "projects", p.Id, patch.GetRawText(), ct);
        return p;
    }

    public async Task DeleteProjectAsync(Guid id, string actor, CancellationToken ct = default)
    {
        var p = await _db.Projects.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct)
                ?? throw new KeyNotFoundException("project not found");
        p.DeletedAt = DateTimeOffset.UtcNow;
        p.UpdatedAt = p.DeletedAt.Value;
        await BumpDataVersion(ct);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(actor, "delete", "projects", p.Id, null, ct);
    }

    // ─── Entries ──────────────────────────────────────────────────────────────

    public async Task<Entry> CreateEntryAsync(JsonElement payload, string actor, CancellationToken ct = default)
    {
        var e = MapEntry(payload, new Entry());
        e.CreatedBy = actor;
        _db.Entries.Add(e);
        EnqueueEmbedding(e.Id);
        await BumpDataVersion(ct);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(actor, "create", "entries", e.Id, null, ct);
        return e;
    }

    public async Task<Entry> UpdateEntryAsync(Guid id, JsonElement patch, string actor, CancellationToken ct = default)
    {
        var e = await _db.Entries.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct)
                ?? throw new KeyNotFoundException("entry not found");
        var before = e.Body;
        MapEntry(patch, e);
        e.Version++;
        e.UpdatedAt = DateTimeOffset.UtcNow;
        EnqueueEmbedding(e.Id);
        await BumpDataVersion(ct);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(actor, "update", "entries", e.Id,
            JsonSerializer.Serialize(new { before, after = e.Body }), ct);
        return e;
    }

    public async Task DeleteEntryAsync(Guid id, string actor, CancellationToken ct = default)
    {
        var e = await _db.Entries.FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct)
                ?? throw new KeyNotFoundException("entry not found");
        e.DeletedAt = DateTimeOffset.UtcNow;
        e.UpdatedAt = e.DeletedAt.Value;
        await BumpDataVersion(ct);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(actor, "delete", "entries", e.Id, null, ct);
    }

    // ─── Links (graph) ────────────────────────────────────────────────────────

    public async Task<EntryLink> AddLinkAsync(Guid fromId, Guid toId, string relation, string actor, CancellationToken ct = default)
    {
        if (!LinkRelations.All.Contains(relation))
            throw new ArgumentException($"bad relation '{relation}'");
        if (!await _db.Entries.AnyAsync(x => x.Id == fromId && x.DeletedAt == null, ct))
            throw new KeyNotFoundException("from entry not found");
        if (!await _db.Entries.AnyAsync(x => x.Id == toId && x.DeletedAt == null, ct))
            throw new KeyNotFoundException("to entry not found");
        var link = await _db.EntryLinks.FindAsync(new object[] { fromId, toId, relation }, ct);
        if (link is not null) return link;
        link = new EntryLink { FromId = fromId, ToId = toId, Relation = relation };
        _db.EntryLinks.Add(link);
        await BumpDataVersion(ct);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(actor, "create", "entry_links", null,
            $"{fromId}->{toId}:{relation}", ct);
        return link;
    }

    public async Task RemoveLinkAsync(Guid fromId, Guid toId, string relation, string actor, CancellationToken ct = default)
    {
        var link = await _db.EntryLinks.FindAsync(new object[] { fromId, toId, relation }, ct)
                   ?? throw new KeyNotFoundException("link not found");
        _db.EntryLinks.Remove(link);
        await BumpDataVersion(ct);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(actor, "delete", "entry_links", null,
            $"{fromId}->{toId}:{relation}", ct);
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static Entry MapEntry(JsonElement el, Entry e)
    {
        if (el.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String)
        {
            var k = kind.GetString()!;
            if (!EntryKinds.All.Contains(k)) throw new ArgumentException($"bad kind '{k}'");
            e.Kind = k;
        }
        if (el.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
            e.Title = title.GetString()!;
        if (el.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String)
            e.Body = body.GetString()!;
        if (el.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            e.Tags = tags.EnumerateArray().Select(t => t.GetString() ?? "").Where(t => t != "").ToList();
        if (el.TryGetProperty("source_url", out var src))
            e.SourceUrl = src.ValueKind == JsonValueKind.Null ? null : src.GetString();
        if (el.TryGetProperty("metadata", out var md))
            e.Metadata = md.ValueKind == JsonValueKind.Null ? null : md.Clone();
        if (el.TryGetProperty("project_id", out var pid) && pid.ValueKind == JsonValueKind.String
            && Guid.TryParse(pid.GetString(), out var guid))
            e.ProjectId = guid;
        else if (el.TryGetProperty("project_slug", out var ps) && ps.ValueKind == JsonValueKind.String)
        {
            // резолв slug → id выполняется вызывающим кодом через FindProjectSlug ниже
        }
        if (string.IsNullOrWhiteSpace(e.Title))
            throw new ArgumentException("entry requires non-empty 'title'");
        return e;
    }

    /// <summary>Разрешает project_slug из payload в ProjectId (если передан).</summary>
    public async Task ResolveProjectSlugAsync(JsonElement payload, Entry e, CancellationToken ct = default)
    {
        if (payload.TryGetProperty("project_slug", out var ps) && ps.ValueKind == JsonValueKind.String)
        {
            var slug = ProposalService.Slugify(ps.GetString()!);
            var proj = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == slug && p.DeletedAt == null, ct);
            if (proj is null) throw new KeyNotFoundException($"project '{slug}' not found");
            e.ProjectId = proj.Id;
        }
    }

    private void EnqueueEmbedding(Guid entryId) => _queue?.Enqueue(entryId);

    private async Task BumpDataVersion(CancellationToken ct)
    {
        var meta = await _db.Meta.FindAsync(new object[] { MetaService.DataVersionKey }, ct);
        if (meta is null)
            _db.Meta.Add(new MetaEntry { Key = MetaService.DataVersionKey, Value = 1 });
        else
            meta.Value++;
    }
}
