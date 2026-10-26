using System.Text.Json;
using Akasha.Core.Data;
using Akasha.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Core.Query;

/// <summary>
/// Трансляция structured JSON Query (§8.2) в EF Core IQueryable — автоматическая параметризация,
/// никаких SQL-инъекций. Whitelist таблиц и полей, timeout 5с, hard limit 1000.
/// </summary>
public class QueryEngine
{
    public const int HardLimit = 1000;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly string[] AllowedTables = { "entries", "projects", "entry_links" };

    // Whitelist полей: таблица → (поле → getter через switch на expression)
    private static readonly HashSet<string> EntryFields = new()
        { "id", "project_id", "kind", "title", "body", "tags", "source_url", "metadata",
          "version", "created_by", "created_at", "updated_at", "deleted_at", "state_hash" };
    private static readonly HashSet<string> ProjectFields = new()
        { "id", "slug", "name", "description", "status", "stack", "links",
          "created_at", "updated_at", "deleted_at" };
    private static readonly HashSet<string> LinkFields = new()
        { "from_id", "to_id", "relation", "created_at" };

    public sealed class QueryException : Exception
    {
        public QueryException(string message) : base(message) { }
    }

    /// <summary>Возвращает JSON Schema формата structured query + примеры (для MCP akasha_query_schema).</summary>
    public static string GetSchemaJson() => SchemaProvider.SchemaJson;

    /// <summary>Проверка формы query без выполнения (для валидации скриптов при сохранении).</summary>
    public void ValidateQueryShape(JsonElement queryEl)
    {
        if (queryEl.ValueKind != JsonValueKind.Object)
            throw new QueryException("Query must be a JSON object.");
        var from = queryEl.TryGetProperty("from", out var f) ? f.GetString() : null;
        if (from is null || !AllowedTables.Contains(from))
            throw new QueryException($"'from' must be one of: {string.Join(", ", AllowedTables)}");
        if (queryEl.TryGetProperty("select", out var sel) && sel.ValueKind != JsonValueKind.Array)
            throw new QueryException("'select' must be an array of field names.");
        if (queryEl.TryGetProperty("where", out var w) && w.ValueKind != JsonValueKind.Array && w.ValueKind != JsonValueKind.Object)
            throw new QueryException("'where' must be an object or array.");
    }

    public async Task<QueryResult> ExecuteAsync(AkashaDbContext db, JsonElement queryEl,
        JsonElement? parameters, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        var token = cts.Token;

        if (queryEl.ValueKind != JsonValueKind.Object)
            throw new QueryException("Query must be a JSON object.");

        var from = queryEl.TryGetProperty("from", out var f) ? f.GetString() : null;
        if (from is null || !AllowedTables.Contains(from))
            throw new QueryException($"'from' must be one of: {string.Join(", ", AllowedTables)}");

        var p = parameters ?? JsonDocument.Parse("{}").RootElement;

        var rows = from switch
        {
            "entries" => await ExecuteEntries(db, queryEl, p, token),
            "projects" => await ExecuteProjects(db, queryEl, p, token),
            "entry_links" => await ExecuteLinks(db, queryEl, p, token),
            _ => throw new QueryException("unreachable")
        };

        return new QueryResult(rows.Item1, rows.Item2);
    }

    // ─── entries ──────────────────────────────────────────────────────────────

    private async Task<(List<Dictionary<string, object?>>, long)> ExecuteEntries(
        AkashaDbContext db, JsonElement q, JsonElement p, CancellationToken ct)
    {
        IQueryable<Entry> iq = db.Entries.AsNoTracking();

        if (q.TryGetProperty("where", out var where))
            iq = ApplyEntryWhere(iq, where, p);

        // group_by + aggregate
        if (q.TryGetProperty("group_by", out var groupBy) && groupBy.ValueKind == JsonValueKind.Array
            && groupBy.GetArrayLength() > 0)
        {
            return await AggregateEntries(iq, groupBy, q, ct);
        }

        // order_by
        var orderBy = q.TryGetProperty("order_by", out var ob) ? ob : default;
        if (orderBy.ValueKind == JsonValueKind.Array && orderBy.GetArrayLength() > 0)
        {
            IOrderedQueryable<Entry>? ordered = null;
            foreach (var o in orderBy.EnumerateArray())
            {
                var field = o.GetProperty("field").GetString()!;
                var desc = o.TryGetProperty("dir", out var d) && d.GetString() == "desc";
                ordered = ordered is null
                    ? (desc ? iq.OrderByDescending(MakeEntryKeySelector(field)) : iq.OrderBy(MakeEntryKeySelector(field)))
                    : (desc ? ordered.ThenByDescending(MakeEntryKeySelector(field)) : ordered.ThenBy(MakeEntryKeySelector(field)));
            }
            if (ordered is not null) iq = ordered;
        }
        else
        {
            iq = iq.OrderByDescending(e => e.UpdatedAt);
        }

        // limit / offset
        var limit = ResolveInt(q.TryGetProperty("limit", out var lim) ? lim : default, p, 50);
        limit = Math.Clamp(limit, 1, HardLimit);
        var offset = ResolveInt(q.TryGetProperty("offset", out var off) ? off : default, p, 0);
        iq = iq.Skip(offset).Take(limit);

        var select = GetSelectFields(q, EntryFields, DefaultEntrySelect);
        var items = await iq.ToListAsync(ct);

        // include links (v1 — только entry_links)
        Dictionary<Guid, List<object>>? linksInclude = null;
        if (q.TryGetProperty("include", out var inc) && inc.TryGetProperty("links", out _))
        {
            var ids = items.Select(i => i.Id).ToList();
            var links = await db.EntryLinks.AsNoTracking()
                .Where(l => ids.Contains(l.FromId))
                .Select(l => new { l.FromId, l.ToId, l.Relation, l.CreatedAt })
                .ToListAsync(ct);
            linksInclude = links.GroupBy(l => l.FromId).ToDictionary(
                g => g.Key,
                g => (List<object>)g.Select(l => (object)new Dictionary<string, object?>
                {
                    ["from_id"] = l.FromId, ["to_id"] = l.ToId,
                    ["relation"] = l.Relation, ["created_at"] = l.CreatedAt
                }).ToList());
        }

        var rows = items.Select(e => ProjectEntry(e, select, linksInclude)).ToList();
        return (rows, rows.Count);
    }

    private static readonly string[] DefaultEntrySelect = { "id", "kind", "title", "tags", "updated_at" };

    private static Dictionary<string, object?> ProjectEntry(Entry e, string[] select,
        Dictionary<Guid, List<object>>? links)
    {
        var row = new Dictionary<string, object?>();
        foreach (var field in select)
        {
            row[field] = field switch
            {
                "id" => e.Id,
                "project_id" => e.ProjectId,
                "kind" => e.Kind,
                "title" => e.Title,
                "body" => e.Body,
                "tags" => e.Tags,
                "source_url" => e.SourceUrl,
                "metadata" => e.Metadata is { } m ? m.Clone().GetRawText() : null,
                "version" => e.Version,
                "created_by" => e.CreatedBy,
                "created_at" => e.CreatedAt,
                "updated_at" => e.UpdatedAt,
                "deleted_at" => e.DeletedAt,
                "state_hash" => e.StateHash,
                _ => null
            };
        }
        if (links is not null) row["links"] = links.TryGetValue(e.Id, out var l) ? l : new List<object>();
        return row;
    }

    private static System.Linq.Expressions.Expression<Func<Entry, object?>> MakeEntryKeySelector(string field)
        => field switch
        {
            "id" => e => e.Id,
            "kind" => e => e.Kind,
            "title" => e => e.Title,
            "updated_at" => e => e.UpdatedAt,
            "created_at" => e => e.CreatedAt,
            "project_id" => e => e.ProjectId,
            "version" => e => e.Version,
            _ => throw new QueryException($"Unknown order_by field for entries: '{field}'")
        };

    private static IQueryable<Entry> ApplyEntryWhere(IQueryable<Entry> iq, JsonElement where, JsonElement p)
    {
        foreach (var prop in where.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "project": // sugar: slug проекта
                {
                    var slug = ResolveString(prop.Value, p);
                    if (slug is not null) iq = iq.Where(e => e.Project != null && e.Project.Slug == slug);
                    break;
                }
                case "project_id":
                {
                    var v = ResolveGuid(prop.Value, p);
                    if (v is not null) iq = iq.Where(e => e.ProjectId == v);
                    break;
                }
                case "id":
                {
                    var v = ResolveGuid(prop.Value, p);
                    if (v is not null) iq = iq.Where(e => e.Id == v);
                    break;
                }
                case "kind":
                {
                    var v = ResolveString(prop.Value, p);
                    if (v is not null) iq = iq.Where(e => e.Kind == v);
                    break;
                }
                case "title":
                {
                    var (op, val) = ExtractOp(prop.Value, p);
                    if (val is not null) iq = op switch
                    {
                        "$eq" => iq.Where(e => e.Title == val),
                        "$ne" => iq.Where(e => e.Title != val),
                        "$like" => iq.Where(e => EF.Functions.Like(e.Title, $"%{val}%")),
                        _ => iq
                    };
                    break;
                }
                case "body":
                {
                    var (op, val) = ExtractOp(prop.Value, p);
                    if (val is not null && op is "$eq" or "$like")
                        iq = iq.Where(e => EF.Functions.Like(e.Body, $"%{val}%"));
                    break;
                }
                case "tags":
                {
                    iq = ApplyTags(iq, prop.Value, p);
                    break;
                }
                case "created_at":
                {
                    iq = ApplyDateCompare(iq, prop.Value, p, e => e.CreatedAt);
                    break;
                }
                case "updated_at":
                {
                    iq = ApplyDateCompare(iq, prop.Value, p, e => e.UpdatedAt);
                    break;
                }
                case "deleted_at":
                {
                    iq = ApplyNullCheck(iq, prop.Value, e => e.DeletedAt);
                    break;
                }
                case "version":
                {
                    var (op, val) = ExtractOp(prop.Value, p);
                    if (val is not null && long.TryParse(val, out var n))
                    {
                        switch (op)
                        {
                            case "$eq": iq = iq.Where(e => e.Version == n); break;
                            case "$gt": iq = iq.Where(e => e.Version > n); break;
                            case "$gte": iq = iq.Where(e => e.Version >= n); break;
                            case "$lt": iq = iq.Where(e => e.Version < n); break;
                            case "$lte": iq = iq.Where(e => e.Version <= n); break;
                        }
                    }
                    break;
                }
                default:
                    throw new QueryException($"Unknown 'where' field for entries: '{prop.Name}'");
            }
        }
        return iq;
    }

    private static IQueryable<Entry> ApplyTags(IQueryable<Entry> iq, JsonElement cond, JsonElement p)
    {
        // Tags хранится как одна строка с разделителем \u001F (ValueConverter).
        // Точное совпадение элемента списка — через LIKE по параметризованному шаблону
        // "%\u001F<tag>\u001F%" (значения идут параметрами SQL, инъекции исключены).
        if (cond.ValueKind == JsonValueKind.String)
        {
            var t = ResolveScalar(cond, p);
            return t is null ? iq : iq.Where(TagPredicate(t));
        }
        if (cond.ValueKind == JsonValueKind.Object)
        {
            foreach (var op in cond.EnumerateObject())
            {
                var vals = op.Value.ValueKind == JsonValueKind.Array
                    ? op.Value.EnumerateArray().Select(x => x.GetString()!).Where(s => !string.IsNullOrEmpty(s)).ToList()
                    : new List<string> { op.Value.GetString()! };
                switch (op.Name)
                {
                    case "$contains":
                        iq = iq.Where(TagPredicate(vals[0]));
                        break;
                    case "$contains_any":
                    {
                        // OR по всем значениям: склеиваем предикаты динамически.
                        var param = System.Linq.Expressions.Expression.Parameter(typeof(Entry), "e");
                        System.Linq.Expressions.Expression? body = null;
                        foreach (var v in vals)
                        {
                            var inner = TagPredicate(v);
                            var inst = new Replacer(inner.Parameters[0], param).Visit(inner.Body);
                            body = body is null ? inst : System.Linq.Expressions.Expression.OrElse(body, inst);
                        }
                        if (body is not null)
                            iq = iq.Where(System.Linq.Expressions.Expression.Lambda<Func<Entry, bool>>(body, param));
                        break;
                    }
                    case "$contains_all":
                    {
                        foreach (var v in vals)
                            iq = iq.Where(TagPredicate(v));
                        break;
                    }
                    default:
                        throw new QueryException($"Unknown tags operator: '{op.Name}'");
                }
            }
        }
        return iq;
    }

    // ─── helpers для тегов (LIKE по сериализованному списку) ────────────────
    internal const char TagSep = '\u001F';

    /// <summary>
    /// Предикат «запись содержит тег». Колонка tags хранит List&lt;string&gt; через ValueConverter,
    /// но для переводимого LIKE используется отдельное поле TagsRaw (та же колонка в raw-виде).
    /// Значение TagsRaw поддерживается синхронно с Tags в AkashaDbContext.SaveChanges*.
    /// </summary>
    private static System.Linq.Expressions.Expression<Func<Entry, bool>> TagPredicate(string tag)
    {
        var pattern = $"%{TagSep}{tag}{TagSep}%";
        return e => EF.Functions.Like(e.TagsRaw!, pattern);
    }

    private sealed class Replacer : System.Linq.Expressions.ExpressionVisitor
    {
        private readonly System.Linq.Expressions.ParameterExpression _from, _to;
        public Replacer(System.Linq.Expressions.ParameterExpression from, System.Linq.Expressions.ParameterExpression to) { _from = from; _to = to; }
        protected override System.Linq.Expressions.Expression VisitParameter(System.Linq.Expressions.ParameterExpression node)
            => node == _from ? _to : base.VisitParameter(node);
    }

    /// <summary>
    /// Публичный хелпер: LIKE-паттерн точного совпадения тега в сериализованном списке (\u001F-разделитель).
    /// Используется SearchService.
    /// </summary>
    internal static string TagLikePattern(string tag) => $"%{TagSep}{tag}{TagSep}%";

    private static IQueryable<Entry> ApplyDateCompare(IQueryable<Entry> iq, JsonElement cond,
        JsonElement p, System.Linq.Expressions.Expression<Func<Entry, DateTimeOffset>> selector)
    {
        var (op, raw) = ExtractOp(cond, p);
        if (raw is null) return iq;
        var dt = ParseDateOrRelative(raw);
        return op switch
        {
            "$eq" => iq.Where(BuildDateLambda(selector, x => x == dt)),
            "$gt" => iq.Where(BuildDateLambda(selector, x => x > dt)),
            "$gte" => iq.Where(BuildDateLambda(selector, x => x >= dt)),
            "$lt" => iq.Where(BuildDateLambda(selector, x => x < dt)),
            "$lte" => iq.Where(BuildDateLambda(selector, x => x <= dt)),
            _ => iq
        };
    }

    private static IQueryable<Entry> ApplyNullCheck(IQueryable<Entry> iq, JsonElement cond,
        System.Linq.Expressions.Expression<Func<Entry, DateTimeOffset?>> selector)
    {
        if (cond.ValueKind == JsonValueKind.Null)
            return iq.Where(BuildNullableDateLambda(selector, x => x == null));
        if (cond.ValueKind == JsonValueKind.Object && cond.TryGetProperty("$is", out var isVal))
        {
            var v = isVal.GetString();
            if (v == "null") return iq.Where(BuildNullableDateLambda(selector, x => x == null));
            if (v == "not_null") return iq.Where(BuildNullableDateLambda(selector, x => x != null));
        }
        return iq;
    }

    private static System.Linq.Expressions.Expression<Func<Entry, bool>> BuildDateLambda(
        System.Linq.Expressions.Expression<Func<Entry, DateTimeOffset>> selector,
        System.Linq.Expressions.Expression<Func<DateTimeOffset, bool>> cmp)
    {
        var param = System.Linq.Expressions.Expression.Parameter(typeof(Entry), "e");
        var body = System.Linq.Expressions.Expression.Invoke(cmp,
            System.Linq.Expressions.Expression.Invoke(selector, param));
        return System.Linq.Expressions.Expression.Lambda<Func<Entry, bool>>(body, param);
    }

    private static System.Linq.Expressions.Expression<Func<Entry, bool>> BuildNullableDateLambda(
        System.Linq.Expressions.Expression<Func<Entry, DateTimeOffset?>> selector,
        System.Linq.Expressions.Expression<Func<DateTimeOffset?, bool>> cmp)
    {
        var param = System.Linq.Expressions.Expression.Parameter(typeof(Entry), "e");
        var body = System.Linq.Expressions.Expression.Invoke(cmp,
            System.Linq.Expressions.Expression.Invoke(selector, param));
        return System.Linq.Expressions.Expression.Lambda<Func<Entry, bool>>(body, param);
    }

    // ─── projects ─────────────────────────────────────────────────────────────

    private async Task<(List<Dictionary<string, object?>>, long)> ExecuteProjects(
        AkashaDbContext db, JsonElement q, JsonElement p, CancellationToken ct)
    {
        IQueryable<Project> iq = db.Projects.AsNoTracking();
        if (q.TryGetProperty("where", out var where))
        {
            foreach (var prop in where.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "slug":
                    {
                        var (op, val) = ExtractOp(prop.Value, p);
                        if (val is not null) iq = op switch
                        {
                            "$eq" => iq.Where(x => x.Slug == val),
                            "$in" => iq.Where(x => PropToArray(prop.Value, p).Contains(x.Slug)),
                            _ => iq
                        };
                        break;
                    }
                    case "status":
                    {
                        var val = ResolveString(prop.Value, p);
                        if (val is not null) iq = iq.Where(x => x.Status == val);
                        break;
                    }
                    case "name":
                    {
                        var val = ResolveString(prop.Value, p);
                        if (val is not null) iq = iq.Where(x => EF.Functions.Like(x.Name, $"%{val}%"));
                        break;
                    }
                    case "deleted_at":
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Null ||
                            (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("$is", out var i) && i.GetString() == "null"))
                            iq = iq.Where(x => x.DeletedAt == null);
                        break;
                    }
                    default:
                        throw new QueryException($"Unknown 'where' field for projects: '{prop.Name}'");
                }
            }
        }

        var limit = Math.Clamp(ResolveInt(q.TryGetProperty("limit", out var lim) ? lim : default, p, 50), 1, HardLimit);
        iq = iq.OrderBy(x => x.Name).Take(limit);
        var select = GetSelectFields(q, ProjectFields, new[] { "id", "slug", "name", "status", "updated_at" });
        var items = await iq.ToListAsync(ct);
        var rows = items.Select(pr =>
        {
            var full = new Dictionary<string, object?>
            {
                ["id"] = pr.Id, ["slug"] = pr.Slug, ["name"] = pr.Name,
                ["description"] = pr.Description, ["status"] = pr.Status, ["stack"] = pr.Stack,
                ["links"] = pr.Links?.GetRawText(),
                ["created_at"] = pr.CreatedAt, ["updated_at"] = pr.UpdatedAt, ["deleted_at"] = pr.DeletedAt
            };
            return full.Where(kv => select.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }).ToList();
        return (rows, rows.Count);
    }

    // ─── entry_links ──────────────────────────────────────────────────────────

    private async Task<(List<Dictionary<string, object?>>, long)> ExecuteLinks(
        AkashaDbContext db, JsonElement q, JsonElement p, CancellationToken ct)
    {
        IQueryable<EntryLink> iq = db.EntryLinks.AsNoTracking();
        if (q.TryGetProperty("where", out var where))
        {
            foreach (var prop in where.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "from_id":
                    {
                        var v = ResolveGuid(prop.Value, p);
                        if (v is not null) iq = iq.Where(l => l.FromId == v);
                        break;
                    }
                    case "to_id":
                    {
                        var v = ResolveGuid(prop.Value, p);
                        if (v is not null) iq = iq.Where(l => l.ToId == v);
                        break;
                    }
                    case "relation":
                    {
                        var v = ResolveString(prop.Value, p);
                        if (v is not null) iq = iq.Where(l => l.Relation == v);
                        break;
                    }
                    default:
                        throw new QueryException($"Unknown 'where' field for entry_links: '{prop.Name}'");
                }
            }
        }
        var limit = Math.Clamp(ResolveInt(q.TryGetProperty("limit", out var lim) ? lim : default, p, 100), 1, HardLimit);
        var items = await iq.Take(limit).ToListAsync(ct);
        var rows = items.Select(l => new Dictionary<string, object?>
        {
            ["from_id"] = l.FromId, ["to_id"] = l.ToId, ["relation"] = l.Relation, ["created_at"] = l.CreatedAt
        }).ToList();
        return (rows, rows.Count);
    }

    // ─── агрегации ────────────────────────────────────────────────────────────

    private static async Task<(List<Dictionary<string, object?>>, long)> AggregateEntries(
        IQueryable<Entry> iq, JsonElement groupBy, JsonElement q, CancellationToken ct)
    {
        var fields = groupBy.EnumerateArray().Select(x => x.GetString()!).ToList();
        if (fields.Count == 1 && fields[0] == "kind")
        {
            return await AggregateSimple(iq.GroupBy(e => e.Kind), q, g => g.Key, "kind");
        }
        if (fields.Count == 1 && fields[0] == "project_id")
        {
            return await AggregateSimple(iq.GroupBy(e => e.ProjectId), q, g => g.Key, "project_id");
        }
        if (fields.Count == 1 && fields[0] == "created_by")
        {
            return await AggregateSimple(iq.GroupBy(e => e.CreatedBy), q, g => g.Key, "created_by");
        }
        throw new QueryException($"Unsupported group_by: [{string.Join(", ", fields)}]. v1 supports kind, project_id, created_by.");
    }

    private static async Task<(List<Dictionary<string, object?>>, long)> AggregateSimple<TKey>(
        IQueryable<IGrouping<TKey, Entry>> grouped, JsonElement q,
        Func<IGrouping<TKey, Entry>, object?> keySel, string keyName)
    {
        var aggs = q.TryGetProperty("aggregate", out var a) && a.ValueKind == JsonValueKind.Object
            ? a : JsonDocument.Parse("{\"count\":{\"$count\":\"*\"}}").RootElement;

        var projections = new List<Dictionary<string, object?>>();
        // Собираем через ToListAsync один раз с materialization групп (безопасно для малых cardinalities)
        var groups = await grouped.ToListAsync();
        foreach (var g in groups)
        {
            var row = new Dictionary<string, object?> { [keyName] = keySel(g) };
            foreach (var agg in aggs.EnumerateObject())
            {
                var fn = agg.Value.EnumerateObject().First();
                row[agg.Name] = fn.Name switch
                {
                    "$count" => (long)g.Count(),
                    "$max" when fn.Value.GetString() == "updated_at" => g.Max(e => (DateTimeOffset?)e.UpdatedAt),
                    "$min" when fn.Value.GetString() == "updated_at" => g.Min(e => (DateTimeOffset?)e.UpdatedAt),
                    "$max" when fn.Value.GetString() == "version" => g.Max(e => (int?)e.Version),
                    "$min" when fn.Value.GetString() == "version" => g.Min(e => (int?)e.Version),
                    "$sum" or "$avg" => throw new QueryException($"Aggregation {fn.Name} on numeric fields is not supported for entries in v1."),
                    _ => throw new QueryException($"Unknown aggregation {fn.Name} on '{fn.Value}'")
                };
            }
            projections.Add(row);
        }

        // order_by по полю агрегата или ключу
        if (q.TryGetProperty("order_by", out var ob) && ob.ValueKind == JsonValueKind.Array && ob.GetArrayLength() > 0)
        {
            var o = ob[0];
            var field = o.GetProperty("field").GetString()!;
            var desc = o.TryGetProperty("dir", out var d) && d.GetString() == "desc";
            projections = desc
                ? projections.OrderByDescending(r => r.GetValueOrDefault(field)).ToList()
                : projections.OrderBy(r => r.GetValueOrDefault(field)).ToList();
        }

        var limit = q.TryGetProperty("limit", out var lim) && lim.TryGetInt32(out var lv) ? lv : 1000;
        return (projections.Take(Math.Min(limit, HardLimit)).ToList(), projections.Count);
    }

    // ─── helpers: параметры, операторы, даты ─────────────────────────────────

    /// <summary>Достаёт (op, scalar value) из значения условия: скаляр / {"$op": value} / {"$param": name}.</summary>
    internal static (string op, string? value) ExtractOp(JsonElement cond, JsonElement p)
    {
        switch (cond.ValueKind)
        {
            case JsonValueKind.String: return ("$eq", cond.GetString());
            case JsonValueKind.Number: return ("$eq", cond.GetRawText());
            case JsonValueKind.True:
            case JsonValueKind.False: return ("$eq", cond.GetRawText());
            case JsonValueKind.Object:
            {
                // одиночный оператор вида {"$gte": ...}
                var first = cond.EnumerateObject().FirstOrDefault();
                if (first.Name is not null && first.Name.StartsWith('$'))
                {
                    if (first.Name == "$param")
                    {
                        var pname = first.Value.GetString();
                        var pv = pname is not null && p.TryGetProperty(pname, out var actual) ? actual : default;
                        return ("$eq", pv.ValueKind switch
                        {
                            JsonValueKind.String => pv.GetString(),
                            JsonValueKind.Number => pv.GetRawText(),
                            JsonValueKind.Null => null,
                            _ => pv.ToString()
                        });
                    }
                    var inner = ResolveScalar(first.Value, p);
                    return (first.Name, inner);
                }
                // просто строковое равенство полям как есть не поддерживаем
                return ("$eq", null);
            }
            default: return ("$eq", null);
        }
    }

    private static string? ResolveScalar(JsonElement v, JsonElement p)
    {
        if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("$param", out var pn))
        {
            var name = pn.GetString();
            return name is not null && p.TryGetProperty(name, out var actual)
                ? actual.ValueKind == JsonValueKind.String ? actual.GetString() : actual.GetRawText()
                : null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => v.ToString()
        };
    }

    private static string? ResolveString(JsonElement cond, JsonElement p)
    {
        var (op, val) = ExtractOp(cond, p);
        if (op == "$in" && cond.ValueKind == JsonValueKind.Object)
        {
            var arr = PropToArray(cond, p);
            return arr.FirstOrDefault();
        }
        return val;
    }

    private static List<string> PropToArray(JsonElement cond, JsonElement p)
    {
        if (cond.ValueKind == JsonValueKind.Array)
            return cond.EnumerateArray().Select(x => x.GetString()!).ToList();
        if (cond.ValueKind == JsonValueKind.Object && cond.TryGetProperty("$in", out var inn) && inn.ValueKind == JsonValueKind.Array)
            return inn.EnumerateArray().Select(x => x.GetString()!).ToList();
        return new List<string>();
    }

    private static Guid? ResolveGuid(JsonElement cond, JsonElement p)
    {
        var s = ResolveString(cond, p);
        return Guid.TryParse(s, out var g) ? g : null;
    }

    private static int ResolveInt(JsonElement cond, JsonElement p, int def)
    {
        if (cond.ValueKind == JsonValueKind.Undefined) return def;
        if (cond.ValueKind == JsonValueKind.Number && cond.TryGetInt32(out var n)) return n;
        var s = ResolveString(cond, p);
        return int.TryParse(s, out var parsed) ? parsed : def;
    }

    /// <summary>$relative: "-7d", "-1h", "-1M", "today", "start_of_week", "start_of_month", "start_of_year".</summary>
    public static DateTimeOffset ParseDateOrRelative(string value)
    {
        var now = DateTimeOffset.UtcNow;
        if (value.StartsWith('-') && value.Length > 2)
        {
            var numStr = value.Substring(1, value.Length - 2);
            if (double.TryParse(numStr, System.Globalization.CultureInfo.InvariantCulture, out var num))
            {
                var unit = value[^1];
                return unit switch
                {
                    'm' => now.AddMinutes(-num),
                    'h' => now.AddHours(-num),
                    'd' => now.AddDays(-num),
                    'w' => now.AddDays(-7 * num),
                    'M' => now.AddMonths((int)-num),
                    'y' => now.AddYears((int)-num),
                    _ => throw new QueryException($"Unknown relative unit '{unit}' in '{value}'")
                };
            }
        }
        switch (value)
        {
            case "today": return now.Date;
            case "start_of_week":
            {
                var d = now.Date;
                var diff = ((int)d.DayOfWeek + 6) % 7; // Monday-first
                return d.AddDays(-diff);
            }
            case "start_of_month": return new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            case "start_of_year": return new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        }
        if (DateTimeOffset.TryParse(value, out var dt)) return dt;
        throw new QueryException($"Cannot parse date/relative value: '{value}'");
    }

    private static string[] GetSelectFields(JsonElement q, HashSet<string> whitelist, string[] defaults)
    {
        if (!q.TryGetProperty("select", out var sel) || sel.ValueKind != JsonValueKind.Array)
            return defaults;
        var fields = sel.EnumerateArray().Select(x => x.GetString()!).ToList();
        foreach (var f in fields)
            if (!whitelist.Contains(f))
                throw new QueryException($"Field '{f}' is not in the whitelist for this table.");
        return fields.ToArray();
    }
}

public record QueryResult(List<Dictionary<string, object?>> Rows, long TotalCount)
{
    public double ElapsedMs { get; init; }
    public bool Cached { get; init; }
}
