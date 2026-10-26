using System.Text.Json;
using Akasha.Core;
using Akasha.Core.Data;
using Akasha.Core.Models;
using Akasha.Core.Query;
using Akasha.Core.Services;
using Akasha.Server.Auth;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Server.Api;

/// <summary>
/// REST API v1 (§5). Единый маппинг эндпоинтов — используется и Server, и Portable
/// (в portable режим owner-session не требуется, доступ только с localhost).
/// </summary>
public static class ApiEndpoints
{
    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapAkashaApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        // ─── Auth (owner login, server mode) ──────────────────────────────────
        api.MapPost("/auth/login", LoginAsync);
        api.MapPost("/auth/logout", Logout);
        api.MapGet("/auth/me", Me);

        // ─── Read (scope: read) ───────────────────────────────────────────────
        api.MapGet("/projects", ListProjectsAsync).RequireScope(Scopes.Read);
        api.MapGet("/projects/{slug}", GetProjectAsync).RequireScope(Scopes.Read);
        api.MapGet("/entries", ListEntriesAsync).RequireScope(Scopes.Read);
        api.MapGet("/entries/{id:guid}", GetEntryAsync).RequireScope(Scopes.Read);
        api.MapGet("/entries/{id:guid}/links", EntryLinksAsync).RequireScope(Scopes.Read);
        api.MapPost("/search", SearchAsync).RequireScope(Scopes.Read);

        // ─── Query & Proposals ────────────────────────────────────────────────
        api.MapPost("/query", RunQueryAsync).RequireScope(Scopes.Read);
        api.MapGet("/query/schema", GetQuerySchema).RequireScope(Scopes.Read);
        api.MapPost("/proposals", CreateProposalAsync).RequireScope(Scopes.Propose);
        api.MapGet("/proposals/{id:guid}", GetProposalAsync).RequireScope(Scopes.Read);
        api.MapGet("/proposals", ListProposalsAsync).RequireScope(Scopes.Read);

        // ─── Admin (owner only) ───────────────────────────────────────────────
        var admin = api.MapGroup("/admin").RequireOwner();
        admin.MapGet("/proposals", AdminListProposalsAsync);
        admin.MapPost("/proposals/{id:guid}/approve", AdminApproveAsync);
        admin.MapPost("/proposals/{id:guid}/reject", AdminRejectAsync);
        admin.MapPost("/proposals/batch-approve", AdminBatchApproveAsync);
        admin.MapGet("/agents", AdminListAgentsAsync);
        admin.MapPost("/agents", AdminCreateAgentAsync);
        admin.MapDelete("/agents/{id:guid}", AdminRevokeAgentAsync);
        admin.MapGet("/audit", AdminAuditAsync);
        admin.MapGet("/secrets", AdminListSecretsAsync);
        admin.MapPost("/secrets", AdminCreateSecretAsync);
        admin.MapGet("/secrets/{id:guid}/reveal", AdminRevealSecretAsync);
        admin.MapDelete("/secrets/{id:guid}", AdminDeleteSecretAsync);

        // Owner CRUD записей (UI владельца)
        admin.MapPost("/projects", AdminCreateProjectAsync);
        admin.MapPut("/projects/{id:guid}", AdminUpdateProjectAsync);
        admin.MapDelete("/projects/{id:guid}", AdminDeleteProjectAsync);
        admin.MapPost("/entries", AdminCreateEntryAsync);
        admin.MapPut("/entries/{id:guid}", AdminUpdateEntryAsync);
        admin.MapDelete("/entries/{id:guid}", AdminDeleteEntryAsync);
        admin.MapPost("/entries/links", AdminAddLinkAsync);
        admin.MapDelete("/entries/links", AdminRemoveLinkAsync);

        // ─── Scripts ──────────────────────────────────────────────────────────
        api.MapGet("/scripts", ListScriptsAsync).RequireScope(Scopes.Scripts);
        api.MapPost("/scripts/{slug}/run", RunScriptAsync).RequireScope(Scopes.Scripts);
        admin.MapPost("/scripts", UpsertScriptAsync);
        admin.MapPut("/scripts/{slug}", UpsertScriptAsync);
        admin.MapPost("/scripts/{slug}/invalidate", InvalidateScriptAsync);
        admin.MapDelete("/scripts/{slug}", DeleteScriptAsync);

        // ─── Sync (device key = agent key со scope sync либо owner session) ──
        var sync = api.MapGroup("/sync");
        sync.MapGet("/pull", SyncPullAsync);
        sync.MapPost("/push", SyncPushAsync);

        // ─── Export / Import (§1.1 воспроизводимость) ─────────────────────────
        admin.MapGet("/export", ExportAsync);
        admin.MapPost("/import", ImportAsync);

        // ─── Health ───────────────────────────────────────────────────────────
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        return app;
    }

    // ═══ helpers ═════════════════════════════════════════════════════════════

    private static Principal P(HttpContext ctx) => (Principal)ctx.Items["principal"]!;
    private static string Actor(HttpContext ctx) => P(ctx).Actor;

    private static IResult Err(int code, string error, string? detail = null) =>
        Results.Json(new ApiTypes.ErrorResponse(error, detail), JsonOpts, statusCode: code);

    // exception → HTTP код по §5.8
    private static IResult FromException(Exception ex) => ex switch
    {
        ProposalService.ConflictException e => Err(409, "conflict", e.Message),
        ProposalService.ValidationException e => Err(422, "validation", e.Message),
        QueryEngine.QueryException e => Err(422, "invalid_query", e.Message),
        SyncService.SyncRejectException e => Err(422, "sync_rejected", e.Message),
        ScriptService.ScriptNotFoundException e => Err(404, "not_found", e.Message),
        KeyNotFoundException e => Err(404, "not_found", e.Message),
        UnauthorizedAccessException e => Err(403, "forbidden", e.Message),
        ArgumentException e => Err(422, "validation", e.Message),
        InvalidOperationException e => Err(409, "conflict", e.Message),
        _ => Err(500, "internal", ex.Message),
    };

    private static async Task<T?> Body<T>(HttpContext ctx) where T : class
    {
        try { return await ctx.Request.ReadFromJsonAsync<T>(JsonOpts); }
        catch (JsonException) { return null; }
    }

    // ═══ auth ════════════════════════════════════════════════════════════════

    public const string SessionCookie = "akasha_session";

    private static async Task<IResult> LoginAsync(HttpContext ctx, IAuthService auth)
    {
        var body = await Body<LoginReq>(ctx);
        if (body is null || string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Password))
            return Err(422, "validation", "email and password required");
        if (!auth.VerifyOwnerPassword(body.Email, body.Password))
            return Err(401, "unauthorized");
        var token = SessionStore.Issue(body.Email);
        ctx.Response.Cookies.Append(SessionCookie, token, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps,
            Expires = DateTimeOffset.UtcNow.AddDays(30),
        });
        return Results.Ok(new { email = body.Email });
    }

    private static IResult Logout(HttpContext ctx)
    {
        ctx.Response.Cookies.Delete(SessionCookie);
        return Results.Ok(new { ok = true });
    }

    private static IResult Me(HttpContext ctx)
    {
        var p = P(ctx);
        return Results.Ok(new
        {
            role = p.IsOwner ? "owner" : "agent",
            name = p.IsOwner ? "owner" : p.Agent!.Name,
            scopes = p.IsOwner ? new List<string> { "admin" } : p.Agent!.Scopes,
        });
    }

    public sealed record LoginReq(string Email, string Password);

    // ═══ read ════════════════════════════════════════════════════════════════

    private static async Task<IResult> ListProjectsAsync(AkashaDbContext db, HttpContext ctx, CancellationToken ct)
    {
        var list = await db.Projects.AsNoTracking()
            .Where(p => p.DeletedAt == null).OrderBy(p => p.Name)
            .ToListAsync(ct);
        return Results.Json(list.Select(p => p.ToDto()), JsonOpts);
    }

    private static async Task<IResult> GetProjectAsync(string slug, AkashaDbContext db, HttpContext ctx, CancellationToken ct)
    {
        var p = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == slug && x.DeletedAt == null, ct);
        if (p is null) return Err(404, "not_found");
        var entries = await db.Entries.AsNoTracking()
            .Where(e => e.ProjectId == p.Id && e.DeletedAt == null)
            .OrderByDescending(e => e.UpdatedAt).Take(100).ToListAsync(ct);
        return Results.Json(new
        {
            project = p.ToDto(),
            entries = entries.Select(e => e.ToDto(p.Slug)),
        }, JsonOpts);
    }

    private static async Task<IResult> ListEntriesAsync(HttpContext ctx, AkashaDbContext db, CancellationToken ct)
    {
        var q = ctx.Request.Query;
        var limit = Math.Clamp(int.TryParse(q["limit"], out var l) ? l : 50, 1, 200);
        int offset = Math.Clamp(int.TryParse(q["cursor"], out var c) ? c : 0, 0, 100000);

        IQueryable<Entry> query = db.Entries.AsNoTracking().Where(e => e.DeletedAt == null);

        if (q["project"].Any())
        {
            var slug = q["project"].ToString();
            var pid = await db.Projects.Where(p => p.Slug == slug).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
            query = query.Where(e => e.ProjectId == pid);
        }
        if (q["kind"].Any())
        {
            var kind = q["kind"].ToString();
            query = query.Where(e => e.Kind == kind);
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(e => e.UpdatedAt).Skip(offset).Take(limit).ToListAsync(ct);
        var slugs = await db.Projects.Where(p => p.DeletedAt == null).ToDictionaryAsync(p => p.Id, p => p.Slug, ct);

        var nextOffset = offset + limit < total ? offset + limit : (int?)null;
        return Results.Json(new
        {
            items = items.Select(e => e.ToDto(e.ProjectId is Guid gid && slugs.TryGetValue(gid, out var s) ? s : null)),
            total,
            next_cursor = nextOffset?.ToString(),
        }, JsonOpts);
    }

    private static async Task<IResult> GetEntryAsync(Guid id, AkashaDbContext db, HttpContext ctx, CancellationToken ct)
    {
        var e = await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (e is null) return Err(404, "not_found");
        string? slug = e.ProjectId is Guid pid
            ? await db.Projects.Where(p => p.Id == pid).Select(p => p.Slug).FirstOrDefaultAsync(ct) : null;
        return Results.Json(e.ToDto(slug), JsonOpts);
    }

    private static async Task<IResult> EntryLinksAsync(Guid id, AkashaDbContext db, HttpContext ctx, CancellationToken ct)
    {
        var links = await db.EntryLinks.AsNoTracking()
            .Where(l => l.FromId == id || l.ToId == id).ToListAsync(ct);
        return Results.Json(links.Select(l => new
        {
            from_id = l.FromId, to_id = l.ToId, relation = l.Relation, created_at = l.CreatedAt,
        }), JsonOpts);
    }

    private static async Task<IResult> SearchAsync(HttpContext ctx, SearchService search, CancellationToken ct)
    {
        var req = await Body<ApiTypes.SearchRequest>(ctx);
        if (req is null || string.IsNullOrWhiteSpace(req.Query))
            return Err(422, "validation", "'query' is required");
        if (req.Mode is not ("hybrid" or "fts" or "vector"))
            return Err(422, "validation", "mode must be hybrid|fts|vector");

        var hits = await search.SearchAsync(req.Query, req.Project, req.Kind, req.Tags, req.Mode,
            Math.Clamp(req.Limit, 1, 100), ct);
        return Results.Json(new ApiTypes.SearchResponse(
            hits.Select(h => new ApiTypes.SearchHit(h.Id, h.Title, h.Snippet, h.Score, h.Kind, h.ProjectSlug)).ToList()),
            JsonOpts);
    }

    // ═══ query & proposals ═══════════════════════════════════════════════════

    private static async Task<IResult> RunQueryAsync(HttpContext ctx, AkashaDbContext db, QueryEngine engine, CancellationToken ct)
    {
        var req = await Body<ApiTypes.QueryRequest>(ctx);
        if (req is null) return Err(422, "validation", "body required");
        try
        {
            var res = await engine.ExecuteAsync(db, req.Query, req.Params, ct);
            object rows = res.Rows;
            if (!string.IsNullOrWhiteSpace(req.Transform))
                rows = JmesTransform.Apply(res.Rows, req.Transform!);
            return Results.Json(new
            {
                rows,
                row_count = res.TotalCount,
                elapsed_ms = Math.Round(res.ElapsedMs, 2),
                cached = false,
            }, JsonOpts);
        }
        catch (Exception ex) { return FromException(ex); }
    }

    private static IResult GetQuerySchema() =>
        Results.Content(QueryEngine.GetSchemaJson(), "application/json");

    private static async Task<IResult> CreateProposalAsync(HttpContext ctx, ProposalService svc, CancellationToken ct)
    {
        var req = await Body<ApiTypes.ProposalRequest>(ctx);
        if (req is null) return Err(422, "validation", "body required");
        try
        {
            var agentId = P(ctx).IsOwner ? (Guid?)null : P(ctx).Agent!.Id;
            var p = await svc.CreateAsync(agentId, req.Op, req.Target, req.TargetId,
                req.Payload, req.BaseVersion, ct);
            return Results.Json(new { id = p.Id, status = p.Status, preview_url = $"/proposals/{p.Id}" }, JsonOpts);
        }
        catch (Exception ex) { return FromException(ex); }
    }

    private static async Task<IResult> GetProposalAsync(Guid id, AkashaDbContext db, HttpContext ctx, CancellationToken ct)
    {
        var p = await db.Proposals.AsNoTracking().Include(x => x.Agent).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new KeyNotFoundException("proposal not found");
        // агент видит только собственные proposals
        var pr = P(ctx);
        if (!pr.IsOwner && p.AgentId != pr.Agent!.Id) return Err(403, "forbidden");
        return Results.Json(p.ToDto(p.Agent?.Name), JsonOpts);
    }

    private static async Task<IResult> ListProposalsAsync(HttpContext ctx, AkashaDbContext db, string? status, CancellationToken ct)
    {
        var pr = P(ctx);
        var q = db.Proposals.AsNoTracking().Include(x => x.Agent).AsQueryable();
        if (!pr.IsOwner) q = q.Where(p => p.AgentId == pr.Agent!.Id);   // агент — только свои
        if (!string.IsNullOrEmpty(status)) q = q.Where(p => p.Status == status);
        var items = await q.OrderByDescending(p => p.CreatedAt).Take(100).ToListAsync(ct);
        return Results.Json(items.Select(p => p.ToDto(p.Agent?.Name)), JsonOpts);
    }

    // ═══ admin ═══════════════════════════════════════════════════════════════

    private static async Task<IResult> AdminListProposalsAsync(AkashaDbContext db, HttpContext ctx, DiffService diff, string? status, CancellationToken ct)
    {
        var q = db.Proposals.AsNoTracking().Include(x => x.Agent).AsQueryable();
        if (!string.IsNullOrEmpty(status)) q = q.Where(p => p.Status == status);
        var items = await q.OrderByDescending(p => p.CreatedAt).Take(200).ToListAsync(ct);
        var result = new List<object>(items.Count);
        foreach (var p in items)
        {
            result.Add(new
            {
                proposal = p.ToDto(p.Agent?.Name),
                diff = await diff.BuildAsync(p, ct),
            });
        }
        return Results.Json(result, JsonOpts);
    }

    private static async Task<IResult> AdminApproveAsync(Guid id, ProposalService svc, HttpContext ctx, CancellationToken ct)
    {
        try { var p = await svc.ApproveAsync(id, ct); return Results.Json(p.ToDto(), JsonOpts); }
        catch (Exception ex) { return FromException(ex); }
    }

    private static async Task<IResult> AdminRejectAsync(Guid id, HttpContext ctx, ProposalService svc, CancellationToken ct)
    {
        var req = await Body<ApiTypes.RejectRequest>(ctx);
        if (req is null || string.IsNullOrWhiteSpace(req.Comment))
            return Err(422, "validation", "comment is required on reject");
        try { var p = await svc.RejectAsync(id, req.Comment, ct); return Results.Json(p.ToDto(), JsonOpts); }
        catch (Exception ex) { return FromException(ex); }
    }

    private static async Task<IResult> AdminBatchApproveAsync(HttpContext ctx, ProposalService svc, CancellationToken ct)
    {
        var req = await Body<ApiTypes.BatchApproveRequest>(ctx);
        if (req is null || req.Ids.Count == 0) return Err(422, "validation", "ids required");
        var approved = new List<Guid>(); var failed = new List<object>();
        foreach (var id in req.Ids)
        {
            try { var p = await svc.ApproveAsync(id, ct); approved.Add(p.Id); }
            catch (Exception ex) { failed.Add(new { id, error = ex.Message }); }
        }
        return Results.Json(new { approved, failed }, JsonOpts);
    }

    private static async Task<IResult> AdminListAgentsAsync(AkashaDbContext db, HttpContext ctx, CancellationToken ct)
    {
        var agents = await db.Agents.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
        return Results.Json(agents.Select(a => a.ToDto()), JsonOpts);
    }

    private static async Task<IResult> AdminCreateAgentAsync(HttpContext ctx, IAuthService auth, AuditService audit, CancellationToken ct)
    {
        var req = await Body<ApiTypes.AgentCreateRequest>(ctx);
        if (req is null || string.IsNullOrWhiteSpace(req.Name)) return Err(422, "validation", "name required");
        try
        {
            var (plaintext, agent) = await auth.CreateAgentAsync(req.Name, req.Description,
                req.Scopes ?? new(), ct);
            await audit.LogAsync(Actor(ctx), "create", "agents", agent.Id, null, ct);
            // plaintext показывается ОДИН РАЗ (§6.2)
            return Results.Json(new { agent = agent.ToDto(), api_key = plaintext }, JsonOpts);
        }
        catch (ArgumentException ex) { return Err(422, "validation", ex.Message); }
    }

    private static async Task<IResult> AdminRevokeAgentAsync(Guid id, IAuthService auth, HttpContext ctx, AuditService audit, CancellationToken ct)
    {
        try
        {
            await auth.RevokeAgentAsync(id, ct);
            await audit.LogAsync(Actor(ctx), "revoke", "agents", id, null, ct);
            return Results.Ok(new { revoked = true });
        }
        catch (KeyNotFoundException ex) { return Err(404, "not_found", ex.Message); }
    }

    private static async Task<IResult> AdminAuditAsync(AkashaDbContext db, AuditService audit, HttpContext ctx,
        string? actor, string? target, DateTimeOffset? since, CancellationToken ct)
    {
        var items = await audit.QueryAsync(db, actor, target, since, 200, ct);
        return Results.Json(items.Select(a => new
        {
            a.Id, a.Actor, a.Action, a.Target, a.TargetId, a.Diff, a.At, a.Ip,
        }), JsonOpts);
    }

    private static byte[] MasterKey(IConfiguration cfg)
    {
        var mk = cfg["AKASHA_MASTER_KEY"] ?? Environment.GetEnvironmentVariable("AKASHA_MASTER_KEY");
        if (string.IsNullOrEmpty(mk)) mk = "akasha-dev-master-key"; // portable/dev fallback
        return SecretCrypto.DeriveKeyFromMasterKey(mk);
    }

    private static async Task<IResult> AdminListSecretsAsync(AkashaDbContext db, HttpContext ctx, Guid? project, CancellationToken ct)
    {
        var q = db.Secrets.AsNoTracking().AsQueryable();
        if (project is not null) q = q.Where(s => s.ProjectId == project);
        var items = await q.OrderBy(s => s.Name).ToListAsync(ct);
        return Results.Json(items.Select(s => s.ToDto()), JsonOpts); // значения не отдаём
    }

    private static async Task<IResult> AdminCreateSecretAsync(HttpContext ctx, AkashaDbContext db, AuditService audit, IConfiguration cfg, CancellationToken ct)
    {
        var req = await Body<ApiTypes.SecretRequest>(ctx);
        if (req is null || string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrEmpty(req.Value))
            return Err(422, "validation", "name and value required");
        var s = new Secret
        {
            ProjectId = req.ProjectId, Name = req.Name, Note = req.Note,
            ValueEncrypted = SecretCrypto.Encrypt(req.Value, MasterKey(cfg)),
        };
        db.Secrets.Add(s);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(Actor(ctx), "create", "secrets", s.Id, null, ct); // diff = null, значение в аудит не пишется
        return Results.Json(s.ToDto(), JsonOpts);
    }

    private static async Task<IResult> AdminRevealSecretAsync(Guid id, AkashaDbContext db, AuditService audit, IConfiguration cfg, HttpContext ctx, CancellationToken ct)
    {
        var s = await db.Secrets.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("secret not found");
        await audit.LogAsync(Actor(ctx), "reveal", "secrets", s.Id, null, ct);
        return Results.Json(new { id = s.Id, name = s.Name, value = SecretCrypto.Decrypt(s.ValueEncrypted, MasterKey(cfg)) }, JsonOpts);
    }

    private static async Task<IResult> AdminDeleteSecretAsync(Guid id, AkashaDbContext db, AuditService audit, HttpContext ctx, CancellationToken ct)
    {
        var s = await db.Secrets.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("secret not found");
        db.Secrets.Remove(s);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(Actor(ctx), "delete", "secrets", s.Id, null, ct);
        return Results.Ok(new { deleted = true });
    }

    // ─── owner CRUD ───────────────────────────────────────────────────────────

    private static async Task<IResult> AdminCreateProjectAsync(HttpContext ctx, RecordsService rec, CancellationToken ct)
    {
        var el = await JsonElementOf(ctx);
        if (el is null || !el.Value.TryGetProperty("name", out var n) || string.IsNullOrWhiteSpace(n.GetString()))
            return Err(422, "validation", "name required");
        try
        {
            var list = el.Value.TryGetProperty("stack", out var st) && st.ValueKind == JsonValueKind.Array
                ? st.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToList() : null;
            var p = await rec.CreateProjectAsync(n.GetString()!,
                el.Value.TryGetProperty("description", out var d) ? d.GetString() : null,
                el.Value.TryGetProperty("slug", out var sl) ? sl.GetString() : null,
                list, Actor(ctx), ct);
            return Results.Json(p.ToDto(), JsonOpts);
        }
        catch (Exception ex) { return FromException(ex); }
    }

    private static async Task<IResult> AdminUpdateProjectAsync(Guid id, HttpContext ctx, RecordsService rec, CancellationToken ct)
    {
        var el = await JsonElementOf(ctx);
        if (el is null) return Err(422, "validation", "body required");
        try { return Results.Json((await rec.UpdateProjectAsync(id, el.Value, Actor(ctx), ct)).ToDto(), JsonOpts); }
        catch (Exception ex) { return FromException(ex); }
    }

    private static async Task<IResult> AdminDeleteProjectAsync(Guid id, RecordsService rec, HttpContext ctx, CancellationToken ct)
    {
        try { await rec.DeleteProjectAsync(id, Actor(ctx), ct); return Results.Ok(new { deleted = true }); }
        catch (Exception ex) { return FromException(ex); }
    }

    private static async Task<IResult> AdminCreateEntryAsync(HttpContext ctx, RecordsService rec, CancellationToken ct)
    {
        var el = await JsonElementOf(ctx);
        if (el is null) return Err(422, "validation", "body required");
        try
        {
            var e = await rec.CreateEntryAsync(el.Value, Actor(ctx), ct);
            return Results.Json(e.ToDto(), JsonOpts);
        }
        catch (Exception ex) { return FromException(ex); }
    }

    private static async Task<IResult> AdminUpdateEntryAsync(Guid id, HttpContext ctx, RecordsService rec, CancellationToken ct)
    {
        var el = await JsonElementOf(ctx);
        if (el is null) return Err(422, "validation", "body required");
        try { return Results.Json((await rec.UpdateEntryAsync(id, el.Value, Actor(ctx), ct)).ToDto(), JsonOpts); }
        catch (Exception ex) { return FromException(ex); }
    }

    private static async Task<IResult> AdminDeleteEntryAsync(Guid id, RecordsService rec, HttpContext ctx, CancellationToken ct)
    {
        try { await rec.DeleteEntryAsync(id, Actor(ctx), ct); return Results.Ok(new { deleted = true }); }
        catch (Exception ex) { return FromException(ex); }
    }

    private static async Task<IResult> AdminAddLinkAsync(HttpContext ctx, RecordsService rec, CancellationToken ct)
    {
        var el = await JsonElementOf(ctx);
        if (el is null) return Err(422, "validation", "body required");
        try
        {
            var r = el.Value.Deserialize<LinkReq>(JsonOpts);
            if (r is null) return Err(422, "validation");
            var link = await rec.AddLinkAsync(r.FromId, r.ToId, r.Relation, Actor(ctx), ct);
            return Results.Json(new { from_id = link.FromId, to_id = link.ToId, relation = link.Relation }, JsonOpts);
        }
        catch (Exception ex) { return FromException(ex); }
    }

    private static async Task<IResult> AdminRemoveLinkAsync(HttpContext ctx, RecordsService rec, CancellationToken ct)
    {
        var el = await JsonElementOf(ctx);
        if (el is null) return Err(422, "validation", "body required");
        try
        {
            var r = el.Value.Deserialize<LinkReq>(JsonOpts);
            if (r is null) return Err(422, "validation");
            await rec.RemoveLinkAsync(r.FromId, r.ToId, r.Relation, Actor(ctx), ct);
            return Results.Ok(new { deleted = true });
        }
        catch (Exception ex) { return FromException(ex); }
    }

    public sealed record LinkReq(Guid FromId, Guid ToId, string Relation);

    private static async Task<JsonElement?> JsonElementOf(HttpContext ctx)
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        return doc.RootElement.Clone();
    }

    // ═══ scripts ═════════════════════════════════════════════════════════════

    private static async Task<IResult> ListScriptsAsync(AkashaDbContext db, HttpContext ctx, CancellationToken ct)
    {
        var pr = P(ctx);
        var q = db.Scripts.AsNoTracking().AsQueryable();
        if (!pr.IsOwner && !pr.HasScope(Scopes.Scripts)) return Err(403, "forbidden");
        var items = await q.OrderBy(s => s.Slug).ToListAsync(ct);
        // агентам не отдаём внутренности больше необходимого? query — публичная декларация, ок.
        return Results.Json(items.Select(s => s.ToDto()), JsonOpts);
    }

    private static async Task<IResult> RunScriptAsync(string slug, HttpContext ctx, ScriptService svc, CancellationToken ct)
    {
        var req = await Body<ApiTypes.ScriptRunRequest>(ctx);
        var prm = req?.Params ?? JsonDocument.Parse("{}").RootElement;
        try
        {
            var r = await svc.RunAsync(slug, prm, ct);
            return Results.Json(new
            {
                result = r.Result,
                cached = r.Cached,
                computed_at = r.ComputedAt,
                script_version = r.ScriptVersion,
                data_version = r.DataVersion,
            }, JsonOpts);
        }
        catch (Exception ex) { return FromException(ex); }
    }

    private static async Task<IResult> UpsertScriptAsync(string? slug, HttpContext ctx, AkashaDbContext db,
        AuditService audit, QueryEngine engine, CancellationToken ct)
    {
        var req = await Body<ApiTypes.ScriptUpsertRequest>(ctx);
        var routeSlug = slug ?? req?.Slug;
        if (req is null || string.IsNullOrWhiteSpace(routeSlug)) return Err(422, "validation", "slug required");
        routeSlug = ProposalService.Slugify(routeSlug);
        try
        {
            // валидация query заранее
            engine.ValidateQueryShape(req.Query);
            var existing = await db.Scripts.FirstOrDefaultAsync(s => s.Slug == routeSlug, ct);
            if (existing is null)
            {
                var s = new Script
                {
                    Slug = routeSlug, Name = req.Name, Description = req.Description,
                    Query = req.Query.Clone(), Transform = req.Transform,
                    OutputFormat = req.OutputFormat ?? "table", ParamsSchema = req.ParamsSchema?.Clone(),
                    CacheTtl = req.CacheTtl ?? 300, CreatedBy = Actor(ctx),
                };
                db.Scripts.Add(s);
                await BumpVersion(db, ct);
                await db.SaveChangesAsync(ct);
                await audit.LogAsync(Actor(ctx), "create", "scripts", s.Id, null, ct);
                return Results.Json(s.ToDto(), JsonOpts);
            }
            existing.Name = req.Name ?? existing.Name;
            existing.Description = req.Description;
            existing.Query = req.Query.Clone();
            existing.Transform = req.Transform;
            existing.OutputFormat = req.OutputFormat ?? existing.OutputFormat;
            existing.ParamsSchema = req.ParamsSchema?.Clone();
            existing.CacheTtl = req.CacheTtl ?? existing.CacheTtl;
            existing.Version++;                    // version++ (§5.5)
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            // инвалидация кэша устаревшей версии
            var stale = await db.ScriptCache.Where(c => c.ScriptId == existing.Id).ToListAsync(ct);
            db.ScriptCache.RemoveRange(stale);
            await BumpVersion(db, ct);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(Actor(ctx), "update", "scripts", existing.Id, null, ct);
            return Results.Json(existing.ToDto(), JsonOpts);
        }
        catch (Exception ex) { return FromException(ex); }
    }

    private static async Task InvalidateVersion(AkashaDbContext db, CancellationToken ct) => await BumpVersion(db, ct);

    private static async Task BumpVersion(AkashaDbContext db, CancellationToken ct)
    {
        var meta = await db.Meta.FindAsync(new object[] { MetaService.DataVersionKey }, ct);
        if (meta is null) db.Meta.Add(new MetaEntry { Key = MetaService.DataVersionKey, Value = 1 });
        else meta.Value++;
    }

    private static async Task<IResult> InvalidateScriptAsync(string slug, ScriptService svc, HttpContext ctx, AuditService audit, CancellationToken ct)
    {
        try { await svc.InvalidateAsync(slug, ct); return Results.Ok(new { invalidated = true }); }
        catch (Exception ex) { return FromException(ex); }
    }

    private static async Task<IResult> DeleteScriptAsync(string slug, AkashaDbContext db, AuditService audit, HttpContext ctx, CancellationToken ct)
    {
        var s = await db.Scripts.FirstOrDefaultAsync(x => x.Slug == slug, ct) ?? throw new KeyNotFoundException("script not found");
        var cache = await db.ScriptCache.Where(c => c.ScriptId == s.Id).ToListAsync(ct);
        db.ScriptCache.RemoveRange(cache);
        db.Scripts.Remove(s);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(Actor(ctx), "delete", "scripts", s.Id, null, ct);
        return Results.Ok(new { deleted = true });
    }

    // ═══ sync ════════════════════════════════════════════════════════════════

    private static async Task<IResult> SyncPullAsync(HttpContext ctx, SyncService svc, DateTimeOffset? since, CancellationToken ct)
    {
        var changes = await svc.PullAsync(since ?? DateTimeOffset.UnixEpoch, ct);
        return Results.Json(new ApiTypes.SyncPullResponse(
            changes.Select(c => new ApiTypes.SyncChangeDto(c.Entity, c.Op, c.Data, c.UpdatedAt)).ToList(),
            DateTimeOffset.UtcNow), JsonOpts);
    }

    private static async Task<IResult> SyncPushAsync(HttpContext ctx, SyncService svc, CancellationToken ct)
    {
        var req = await Body<ApiTypes.SyncPushRequest>(ctx);
        if (req is null || string.IsNullOrWhiteSpace(req.DeviceId)) return Err(422, "validation", "device_id required");
        try
        {
            var changes = req.Changes.Select(c => new SyncService.SyncChange(c.Entity, c.Op, c.Data, c.UpdatedAt)).ToList();
            var res = await svc.PushAsync(changes, req.DeviceId, ct);
            return Results.Json(new { applied = res.Applied, skipped_older = res.SkippedOlder, conflict_proposals = res.ConflictProposals }, JsonOpts);
        }
        catch (Exception ex) { return FromException(ex); }
    }

    // ═══ export / import ═════════════════════════════════════════════════════

    private static async Task<IResult> ExportAsync(AkashaDbContext db, HttpContext ctx, bool includeSecrets, CancellationToken ct)
    {
        var svc = new ExportService(db);
        var ms = new MemoryStream();
        await svc.ExportAsync(ms, includeSecrets, ct);
        ms.Position = 0;
        ctx.Items["skip_rate_limit"] = true;
        return Results.File(ms, "application/zip",
            $"akasha-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
    }

    private static async Task<IResult> ImportAsync(HttpContext ctx, AkashaDbContext db, CancellationToken ct)
    {
        if (!ctx.Request.HasFormContentType) return Err(422, "validation", "multipart form with 'file' expected");
        var form = await ctx.Request.ReadFormAsync(ct);
        var file = form.Files["file"] ?? throw new ArgumentException("file part required");
        await using var stream = file.OpenReadStream();
        var svc = new ExportService(db);
        var count = await svc.ImportAsync(stream, ct);
        return Results.Ok(new { imported = count });
    }
}
