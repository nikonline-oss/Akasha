using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Akasha.Core.Data;
using Akasha.Core.Models;
using Akasha.Core.Query;
using Microsoft.EntityFrameworkCore;
using ZiggyCreatures.Caching.Fusion;

namespace Akasha.Core.Services;

/// <summary>Доступ к meta.data_version (§3.3) — O(1).</summary>
public class MetaService
{
    public const string DataVersionKey = "data_version";
    private readonly IDbContextFactoryLike _factory;

    public MetaService(IDbContextFactoryLike factory) => _factory = factory;

    public async Task<long> GetDataVersionAsync(CancellationToken ct = default)
    {
        using var db = _factory.Create();
        var m = await db.Meta.AsNoTracking().FirstOrDefaultAsync(x => x.Key == DataVersionKey, ct);
        return m?.Value ?? 0;
    }

    public async Task<long> GetDataVersionAsync(AkashaDbContext db, CancellationToken ct = default)
    {
        var m = await db.Meta.AsNoTracking().FirstOrDefaultAsync(x => x.Key == DataVersionKey, ct);
        return m?.Value ?? 0;
    }
}

/// <summary>
/// Исполнение скриптов с кэшем по sha256(script_id | v{version} | params | dv{data_version}) (§8.4).
/// </summary>
public class ScriptService
{
    private readonly AkashaDbContext _db;
    private readonly QueryEngine _engine;
    private readonly MetaService _meta;
    private readonly AuditService _audit;
    private readonly IFusionCache _cache;

    public ScriptService(AkashaDbContext db, QueryEngine engine, MetaService meta, AuditService audit, IFusionCache cache)
    {
        _db = db; _engine = engine; _meta = meta; _audit = audit; _cache = cache;
    }

    public record ScriptRunResult(JsonElement Result, bool Cached, DateTimeOffset ComputedAt,
        int ScriptVersion, long DataVersion, double ElapsedMs);

    public sealed class ScriptNotFoundException : Exception
    {
        public ScriptNotFoundException(string slug) : base($"script '{slug}' not found") { }
    }

    public async Task<ScriptRunResult> RunAsync(string slug, JsonElement @params, CancellationToken ct = default)
    {
        var script = await _db.Scripts.AsNoTracking().FirstOrDefaultAsync(s => s.Slug == slug, ct)
                     ?? throw new ScriptNotFoundException(slug);

        ValidateParamsAgainstSchema(script.ParamsSchema, @params);

        var dataVersion = await _meta.GetDataVersionAsync(ct);
        var cacheKey = BuildCacheKey(script.Id, script.Version, @params, dataVersion);
        var ttl = TimeSpan.FromSeconds(Math.Max(script.CacheTtl, 1));

        // L1: FusionCache (in-memory).
        var l1 = await _cache.GetOrDefaultAsync<ScriptRunResult>(cacheKey, token: ct);
        if (l1 is not null) return l1 with { Cached = true };

        // L2: таблица script_cache (переживает рестарт приложения).
        // Ключ уникален — берём запись и проверяем срок годности в памяти
        // (сравнения DateTimeOffset в LINQ-to-SQL провайдером SQLite не транслируются).
        var cacheCandidate = await _db.ScriptCache.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CacheKey == cacheKey, ct);
        var cachedItem = cacheCandidate is not null && cacheCandidate.ExpiresAt > DateTimeOffset.UtcNow
            ? cacheCandidate : null;
        if (cachedItem is not null)
        {
            var res = new ScriptRunResult(JsonDocument.Parse(cachedItem.ResultJson).RootElement.Clone(),
                true, cachedItem.ComputedAt, cachedItem.ScriptVersion, cachedItem.DataVersion, 0);
            await _cache.SetAsync(cacheKey, res, ttl, token: ct);
            return res;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _engine.ExecuteAsync(_db, script.Query, @params.ValueKind == JsonValueKind.Undefined ? null : @params, ct);
        sw.Stop();

        object payloadObj = result.Rows;
        // JMESPath transform (sandboxed: только выражение над результатом, без file/http/process)
        if (!string.IsNullOrWhiteSpace(script.Transform))
        {
            payloadObj = JmesTransform.Apply(result.Rows, script.Transform);
        }

        var envelope = JsonSerializer.SerializeToElement(new
        {
            rows = payloadObj,
            row_count = result.Rows.Count,
            elapsed_ms = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            output_format = script.OutputFormat
        });

        // сохраняем в кэш (перезаписываем промахнувшийся ключ)
        var item = new ScriptCacheItem
        {
            CacheKey = cacheKey,
            ScriptId = script.Id,
            ResultJson = envelope.GetRawText(),
            ComputedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(script.CacheTtl, 1)),
            ScriptVersion = script.Version,
            DataVersion = dataVersion
        };
        try
        {
            var existing = await _db.ScriptCache.FindAsync(new object[] { cacheKey }, ct);
            if (existing is not null) _db.ScriptCache.Remove(existing);
            _db.ScriptCache.Add(item);
            await _db.SaveChangesAsync(ct);
        }
        catch { /* cache write is best-effort */ }

        var computed = new ScriptRunResult(envelope.Clone(), false, item.ComputedAt, script.Version, dataVersion,
            sw.Elapsed.TotalMilliseconds);
        await _cache.SetAsync(cacheKey, computed, ttl, token: ct);
        return computed;
    }

    public async Task InvalidateAsync(string slug, CancellationToken ct = default)
    {
        var script = await _db.Scripts.FirstOrDefaultAsync(s => s.Slug == slug, ct)
                     ?? throw new ScriptNotFoundException(slug);
        var stale = await _db.ScriptCache.Where(c => c.ScriptId == script.Id).ToListAsync(ct);
        _db.ScriptCache.RemoveRange(stale);
        await _db.SaveChangesAsync(ct);
        _cache.Clear();
        await _audit.LogAsync("owner", "invalidate", "scripts", script.Id, null, ct);
    }

    public static string BuildCacheKey(Guid scriptId, int version, JsonElement @params, long dataVersion)
    {
        var canonical = CanonicalJson(@params);
        var raw = $"{scriptId}|v{version}|{canonical}|dv{dataVersion}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    /// <summary>Канонический JSON: сортировка ключей — стабильный хеш параметров.</summary>
    public static string CanonicalJson(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                return "{" + string.Join(",", el.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => JsonSerializer.Serialize(p.Name) + ":" + CanonicalJson(p.Value))) + "}";
            case JsonValueKind.Array:
                return "[" + string.Join(",", el.EnumerateArray().Select(CanonicalJson)) + "]";
            default:
                return el.GetRawText();
        }
    }

    /// <summary>Минимальная валидация параметров по JSON Schema (type/enum/required/min/max для v1).</summary>
    public static void ValidateParamsAgainstSchema(JsonElement? schema, JsonElement @params)
    {
        if (schema is null || schema.Value.ValueKind != JsonValueKind.Object) return;
        var s = schema.Value;
        if (s.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in req.EnumerateArray())
            {
                var name = r.GetString()!;
                if (@params.ValueKind != JsonValueKind.Object || !@params.TryGetProperty(name, out _))
                    throw new QueryEngine.QueryException($"missing required parameter '{name}'");
            }
        }
        if (s.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object &&
            @params.ValueKind == JsonValueKind.Object)
        {
            foreach (var pv in @params.EnumerateObject())
            {
                if (!props.TryGetProperty(pv.Name, out var ps) || ps.ValueKind != JsonValueKind.Object) continue;
                if (ps.TryGetProperty("type", out var t))
                {
                    var expected = t.GetString();
                    var ok = expected switch
                    {
                        "string" => pv.Value.ValueKind == JsonValueKind.String,
                        "integer" => pv.Value.ValueKind == JsonValueKind.Number && pv.Value.TryGetInt32(out _),
                        "number" => pv.Value.ValueKind == JsonValueKind.Number,
                        "boolean" => pv.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                        "object" => pv.Value.ValueKind == JsonValueKind.Object,
                        "array" => pv.Value.ValueKind == JsonValueKind.Array,
                        _ => true
                    };
                    if (!ok) throw new QueryEngine.QueryException($"parameter '{pv.Name}' must be of type '{expected}'");
                }
                if (ps.TryGetProperty("enum", out var en) && en.ValueKind == JsonValueKind.Array)
                {
                    var allowed = en.EnumerateArray().Select(x => x.GetRawText()).ToList();
                    if (!allowed.Contains(pv.Value.GetRawText()))
                        throw new QueryEngine.QueryException($"parameter '{pv.Name}' must be one of enum values");
                }
                if (pv.Value.ValueKind == JsonValueKind.Number)
                {
                    if (ps.TryGetProperty("minimum", out var mn) && pv.Value.GetDouble() < mn.GetDouble())
                        throw new QueryEngine.QueryException($"parameter '{pv.Name}' below minimum");
                    if (ps.TryGetProperty("maximum", out var mx) && pv.Value.GetDouble() > mx.GetDouble())
                        throw new QueryEngine.QueryException($"parameter '{pv.Name}' above maximum");
                }
            }
        }
    }
}
