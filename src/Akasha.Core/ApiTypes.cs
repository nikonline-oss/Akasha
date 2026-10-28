using System.Text.Json;

namespace Akasha.Core;

/// <summary>
/// DTO контрактов API (§5). Лежат в Core, чтобы REST-слой сервера, MCP и portable
/// использовали одни и те же модели запросов/ответов.
/// </summary>
public static class ApiTypes
{
    // ─── Search ───────────────────────────────────────────────────────────────
    public sealed record SearchRequest(
        string Query,
        string? Project = null,
        string? Kind = null,
        List<string>? Tags = null,
        string Mode = "hybrid",          // hybrid | fts | vector
        int Limit = 20);

    public sealed record SearchResponse(List<SearchHit> Hits);
    public sealed record SearchHit(Guid Id, string Title, string Snippet, double Score,
        string Kind, string? ProjectSlug);

    // ─── Query ────────────────────────────────────────────────────────────────
    public sealed record QueryRequest(
        JsonElement Query,
        JsonElement? Params = null,
        string? Transform = null);

    public sealed record QueryResponse(
        JsonElement Rows,
        long RowCount,
        double ElapsedMs,
        bool Cached);

    // ─── Proposals ────────────────────────────────────────────────────────────
    public sealed record ProposalRequest(
        string Op,
        string Target,
        Guid? TargetId,
        JsonElement Payload,
        int? BaseVersion);

    public sealed record ProposalDto(
        Guid Id,
        string Op,
        string Target,
        Guid? TargetId,
        JsonElement Payload,
        int? BaseVersion,
        string Status,
        string? ReviewComment,
        DateTimeOffset? ReviewedAt,
        DateTimeOffset CreatedAt,
        string? AgentName);

    public static ProposalDto ToDto(this Models.Proposal p, string? agentName = null) => new(
        p.Id, p.Op, p.Target, p.TargetId, p.Payload.Clone(), p.BaseVersion,
        p.Status, p.ReviewComment, p.ReviewedAt, p.CreatedAt, agentName);

    // ─── Projects / Entries ───────────────────────────────────────────────────
    public sealed record ProjectDto(
        Guid Id, string Slug, string Name, string? Description, string Status,
        List<string> Stack, JsonElement? Links,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    public static ProjectDto ToDto(this Models.Project p) => new(
        p.Id, p.Slug, p.Name, p.Description, p.Status, p.Stack,
        p.Links?.Clone(), p.CreatedAt, p.UpdatedAt);

    /// <summary>Entry для агентов: embedding никогда не отдаётся (внутреннее поле).</summary>
    public sealed record EntryDto(
        Guid Id, Guid? ProjectId, string? ProjectSlug, string Kind, string Title,
        string Body, List<string> Tags, string? SourceUrl, JsonElement? Metadata,
        int Version, string? CreatedBy,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    public static EntryDto ToDto(this Models.Entry e, string? projectSlug = null) => new(
        e.Id, e.ProjectId, projectSlug, e.Kind, e.Title, e.Body, e.Tags, e.SourceUrl,
        e.Metadata?.Clone(), e.Version, e.CreatedBy, e.CreatedAt, e.UpdatedAt);

    // ─── Scripts ──────────────────────────────────────────────────────────────
    public sealed record ScriptDto(
        Guid Id, string Slug, string Name, string? Description,
        JsonElement Query, string? Transform, string OutputFormat,
        JsonElement? ParamsSchema, int Version, int CacheTtl,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    public static ScriptDto ToDto(this Models.Script s) => new(
        s.Id, s.Slug, s.Name, s.Description, s.Query.Clone(), s.Transform, s.OutputFormat,
        s.ParamsSchema?.Clone(), s.Version, s.CacheTtl, s.CreatedAt, s.UpdatedAt);

    public sealed record ScriptRunRequest(JsonElement? Params);

    public sealed record ScriptUpsertRequest(
        string Slug, string Name, string? Description,
        JsonElement Query, string? Transform, string? OutputFormat,
        JsonElement? ParamsSchema, int? CacheTtl);

    // ─── Context store (агенты пишут без апрувов) ─────────────────────────────
    public sealed record ContextCreateRequest(Guid? ProjectId, string Title, string? Body, List<string>? Tags);

    /// <summary>Частичное обновление записи контекста: null-поля остаются как были.</summary>
    public sealed record ContextUpdateRequest(string? Title, string? Body, List<string>? Tags);

    public sealed record ContextDto(
        Guid Id, Guid? AgentId, Guid? ProjectId, string Title, string Body,
        List<string> Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    public static ContextDto ToDto(this Models.ContextNote n) => new(
        n.Id, n.AgentId, n.ProjectId, n.Title, n.Body, n.Tags, n.CreatedAt, n.UpdatedAt);

    // ─── Sync ─────────────────────────────────────────────────────────────────
    public sealed record SyncPushRequest(string DeviceId, List<SyncChangeDto> Changes);
    public sealed record SyncChangeDto(string Entity, string Op, JsonElement Data, DateTimeOffset UpdatedAt);
    public sealed record SyncPullResponse(List<SyncChangeDto> Changes, DateTimeOffset ServerTime);

    // ─── Admin ────────────────────────────────────────────────────────────────
    public sealed record AgentCreateRequest(string Name, string? Description, List<string> Scopes);
    public sealed record AgentDto(Guid Id, string Name, string? Description, string KeyPrefix,
        List<string> Scopes, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt,
        string? LastUsedIp, DateTimeOffset? RevokedAt);
    public static AgentDto ToDto(this Models.Agent a) => new(
        a.Id, a.Name, a.Description, a.KeyPrefix, a.Scopes,
        a.CreatedAt, a.LastUsedAt, a.LastUsedIp, a.RevokedAt);

    public sealed record RejectRequest(string Comment);
    public sealed record BatchApproveRequest(List<Guid> Ids);

    public sealed record SecretRequest(Guid? ProjectId, string Name, string Value, string? Note);
    /// <summary>Секрет наружу — только метаданные, значение НЕ отдаётся без явного запроса.</summary>
    public sealed record SecretDto(Guid Id, Guid? ProjectId, string Name, string? Note,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    public static SecretDto ToDto(this Models.Secret s) => new(
        s.Id, s.ProjectId, s.Name, s.Note, s.CreatedAt, s.UpdatedAt);

    // ─── Errors ───────────────────────────────────────────────────────────────
    public sealed record ErrorResponse(string Error, string? Detail = null);
}
