using Akasha.Core.Data;
using Akasha.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Core.Services;

/// <summary>Аудит всех операций owner и agents (§3.1 audit_log).</summary>
public class AuditService
{
    private readonly IDbContextFactoryLike _factory;

    public AuditService(IDbContextFactoryLike factory) => _factory = factory;

    /// <summary>Пишет в audit_log отдельным контекстом — чтобы не ломать транзакцию вызывающего.</summary>
    public async Task LogAsync(string actor, string action, string target, Guid? targetId,
        string? diffJson = null, CancellationToken ct = default)
    {
        try
        {
            using var db = _factory.Create();
            db.AuditLog.Add(new AuditLog
            {
                Actor = actor,
                Action = action,
                Target = target,
                TargetId = targetId,
                Diff = diffJson,
                At = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }
        catch { /* audit must never break the main flow */ }
    }

    public async Task<List<AuditLog>> QueryAsync(AkashaDbContext db, string? actor = null,
        string? target = null, DateTimeOffset? since = null, int limit = 100, CancellationToken ct = default)
    {
        var iq = db.AuditLog.AsNoTracking().AsQueryable();
        if (actor is not null) iq = iq.Where(a => a.Actor == actor);
        if (target is not null) iq = iq.Where(a => a.Target == target);
        if (since is { } s) iq = iq.Where(a => a.At >= s);
        return await iq.OrderByDescending(a => a.Id).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
    }
}
