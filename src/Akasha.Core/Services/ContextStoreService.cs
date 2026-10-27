using Akasha.Core.Data;
using Akasha.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Core.Services;

/// <summary>
/// «Хранилище контекста» агентов: прямые write без апрувов, отдельно от основного
/// хранилища (<c>entries</c>). Доступ даётся scope <c>context</c>.
/// </summary>
public class ContextStoreService
{
    private readonly AkashaDbContext _db;
    private readonly AuditService _audit;

    public ContextStoreService(AkashaDbContext db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<ContextNote> CreateAsync(string actor, Guid? agentId, Guid? projectId,
        string title, string body, IEnumerable<string>? tags, CancellationToken ct = default)
    {
        var note = new ContextNote
        {
            AgentId = agentId,
            ProjectId = projectId,
            Title = (title ?? "").Trim(),
            Body = body ?? "",
            Tags = (tags ?? Array.Empty<string>())
                .Select(t => (t ?? "").Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
        _db.ContextNotes.Add(note);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(actor, "context.create", "context_notes", note.Id, null, ct);
        return note;
    }

    public Task<ContextNote?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.ContextNotes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task<List<ContextNote>> ListAsync(Guid? projectId, Guid? agentId, string? query,
        int limit = 100, CancellationToken ct = default)
    {
        var q = _db.ContextNotes.AsNoTracking();
        if (projectId is Guid p) q = q.Where(x => x.ProjectId == p);
        if (agentId is Guid a) q = q.Where(x => x.AgentId == a);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = query.Trim();
            q = q.Where(x => x.Title.Contains(needle) || x.Body.Contains(needle));
        }
        return await q.OrderByDescending(x => x.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
    }

    public async Task<bool> DeleteAsync(string actor, Guid id, Guid? agentId, CancellationToken ct = default)
    {
        var note = await _db.ContextNotes.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (note is null) return false;
        // агент удаляет только свои записи; владелец (agentId == null) — любые.
        if (agentId is Guid a && note.AgentId != a)
            throw new UnauthorizedAccessException("agents can delete only their own context notes");
        _db.ContextNotes.Remove(note);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(actor, "context.delete", "context_notes", id, null, ct);
        return true;
    }
}
