using System.Text.Json;
using System.Text.Json.Nodes;
using Akasha.Core.Data;
using Akasha.Core.Models;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Akasha.Core.Services;

/// <summary>Гибридный поиск: FTS (LIKE/词 overlap) + vector cosine → RRF fusion (§8.1).</summary>
public class SearchService
{
    private readonly AkashaDbContext _db;
    private readonly IEmbeddingService _embeddings;

    public SearchService(AkashaDbContext db, IEmbeddingService embeddings)
    {
        _db = db;
        _embeddings = embeddings;
    }

    public record Hit(Guid Id, string Title, string Snippet, double Score, string Kind, string? ProjectSlug);

    public async Task<List<Hit>> SearchAsync(string query, string? project = null, string? kind = null,
        List<string>? tags = null, string mode = "hybrid", int limit = 20, CancellationToken ct = default)
    {
        mode = mode.ToLowerInvariant();
        limit = Math.Clamp(limit, 1, 100);

        IQueryable<Entry> Base()
        {
            var iq = _db.Entries.AsNoTracking().Where(e => e.DeletedAt == null);
            if (project is not null) iq = iq.Where(e => e.Project != null && e.Project.Slug == project);
            if (kind is not null) iq = iq.Where(e => e.Kind == kind);
            return iq;
        }

        // Фильтр по тегам применяем в памяти: колонка tags хранится как сериализованный
        // список (ValueConverter), и List<string>.Contains не транслируется провайдером.
        var activeTags = tags?.Where(t => !string.IsNullOrEmpty(t)).ToList() ?? new List<string>();
        bool TagMatch(Entry e) => activeTags.Count == 0 || activeTags.Any(t => (e.Tags ?? new()).Contains(t));

        // ── FTS-ранк: пословное вхождение в title/body (simple tokenizer, EN/RU) ──
        var terms = query.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '"', '\'' },
                StringSplitOptions.RemoveEmptyEntries)
            .Distinct().Take(12).ToArray();

        var ftsRanked = new List<(Entry e, double score)>();
        if (mode is "fts" or "hybrid")
        {
            var candidates = await Base().ToListAsync(ct);
            if (activeTags.Count > 0) candidates = candidates.Where(TagMatch).ToList();
            foreach (var e in candidates)
            {
                var hay = (e.Title + " " + e.Body).ToLowerInvariant();
                double s = 0;
                foreach (var t in terms)
                {
                    if (e.Title.ToLowerInvariant().Contains(t)) s += 2.0;
                    else if (hay.Contains(t)) s += 1.0;
                }
                if (s > 0) ftsRanked.Add((e, s));
            }
            ftsRanked = ftsRanked.OrderByDescending(x => x.score).Take(100).ToList();
        }

        // ── Vector-ранк: cosine по embedding ──
        var vecRanked = new List<(Entry e, double score)>();
        if (mode is "vector" or "hybrid")
        {
            var qv = _embeddings.Embed(query);
            var candidates = await LoadVectorCandidatesAsync(Base(), qv, ct);
            if (activeTags.Count > 0) candidates = candidates.Where(TagMatch).ToList();
            vecRanked = RankVectors(candidates, qv);
        }

        // ── RRF fusion: score(d) = Σ 1/(60 + rank_i(d)) ──
        const int k = 60;
        var fused = new Dictionary<Guid, (Entry e, double score)>();
        for (int i = 0; i < ftsRanked.Count; i++)
        {
            var (e, _) = ftsRanked[i];
            var cur = fused.TryGetValue(e.Id, out var c) ? c.score : 0.0;
            fused[e.Id] = (e, cur + 1.0 / (k + i + 1));
        }
        for (int i = 0; i < vecRanked.Count; i++)
        {
            var (e, _) = vecRanked[i];
            var cur = fused.TryGetValue(e.Id, out var c) ? c.score : 0.0;
            fused[e.Id] = (e, cur + 1.0 / (k + i + 1));
        }

        var slugs = await _db.Projects.AsNoTracking()
            .ToDictionaryAsync(p => p.Id, p => p.Slug, ct);

        return fused.Values
            .OrderByDescending(x => x.score)
            .Take(limit)
            .Select(x => new Hit(x.e.Id, x.e.Title, MakeSnippet(x.e.Body, terms),
                Math.Round(x.score, 6), x.e.Kind,
                x.e.ProjectId is { } pid && slugs.TryGetValue(pid, out var slug) ? slug : null))
            .ToList();
    }

    /// <summary>
    /// Кандидаты для векторного ранга. Postgres — prefilter по pgvector (top-500), SQLite — все.
    /// При любой ошибке pgvector-пути — грузим всё в память.
    /// </summary>
    private async Task<List<Entry>> LoadVectorCandidatesAsync(IQueryable<Entry> baseQuery, float[] qv, CancellationToken ct)
    {
        if (_db.IsSqlite) return await baseQuery.Where(e => e.Embedding != null).ToListAsync(ct);
        try
        {
            var qvec = new Vector(qv);
            return await baseQuery
                .Where(e => e.Embedding != null)
                .OrderBy(e => e.Embedding!.CosineDistance(qvec))
                .Take(500)
                .ToListAsync(ct);
        }
        catch
        {
            return await baseQuery.Where(e => e.Embedding != null).ToListAsync(ct);
        }
    }

    /// <summary>Слишком короткие тексты дают «hub» (близки ко всему) — их находит FTS.</summary>
    private const int MinVectorWords = 4;

    /// <summary>
    /// Ранжирование cosine с центрированием по среднему вектору корпуса
    /// (устраняет анизотропию/«hubness» коротких записей).
    /// </summary>
    private static List<(Entry e, double score)> RankVectors(List<Entry> candidates, float[] qv)
    {
        var docs = candidates
            .Where(e => e.Embedding is not null && DistinctWords(e.Title + " " + e.Body) >= MinVectorWords)
            .ToList();
        if (docs.Count == 0) return new();

        var dim = qv.Length;
        var mean = new double[dim];
        foreach (var d in docs)
        {
            var v = d.Embedding!.ToArray();
            for (int i = 0; i < dim && i < v.Length; i++) mean[i] += v[i];
        }
        for (int i = 0; i < dim; i++) mean[i] /= docs.Count;

        float[] Center(float[] v)
        {
            var r = new float[dim];
            for (int i = 0; i < dim && i < v.Length; i++) r[i] = (float)(v[i] - mean[i]);
            return r;
        }

        var qc = Center(qv);
        var list = new List<(Entry e, double score)>();
        foreach (var d in docs)
        {
            var cos = HashedNgramEmbeddings.Cosine(qc, Center(d.Embedding!.ToArray()));
            if (cos > 0.01) list.Add((d, cos));
        }
        return list.OrderByDescending(x => x.score).Take(100).ToList();
    }

    private static int DistinctWords(string s) =>
        string.IsNullOrWhiteSpace(s) ? 0
        : s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant()).Distinct().Count();

    private static string MakeSnippet(string body, string[] terms)
    {
        var lower = body.ToLowerInvariant();
        var idx = -1;
        foreach (var t in terms)
        {
            idx = lower.IndexOf(t);
            if (idx >= 0) break;
        }
        if (idx < 0) idx = 0;
        var start = Math.Max(0, idx - 60);
        var len = Math.Min(200, body.Length - start);
        var snip = body[start..(start + len)].Replace('\n', ' ').Trim();
        return (start > 0 ? "…" : "") + snip + (start + len < body.Length ? "…" : "");
    }
}

/// <summary>Фоновый пересчёт embedding'ов (§8.1): IHostedService + Channel&lt;Guid&gt;.</summary>
public class EmbeddingQueue
{
    private readonly System.Threading.Channels.Channel<Guid> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<Guid>();

    public void Enqueue(Guid entryId) => _channel.Writer.TryWrite(entryId);
    public System.Threading.Channels.ChannelReader<Guid> Reader => _channel.Reader;
}

public class EmbeddingWorker : BackgroundWorkerBase
{
    private readonly EmbeddingQueue _queue;
    private readonly IEmbeddingService _embeddings;
    private readonly IDbContextFactoryLike _dbFactory;

    public record struct DbHandle(AkashaDbContext Db);

    public EmbeddingWorker(EmbeddingQueue queue, IEmbeddingService embeddings, IDbContextFactoryLike dbFactory)
    {
        _queue = queue; _embeddings = embeddings; _dbFactory = dbFactory;
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                using var db = _dbFactory.Create();
                var entry = await db.Entries.FirstOrDefaultAsync(e => e.Id == id, ct);
                if (entry is null || entry.DeletedAt is not null) continue;
                entry.Embedding = new Vector(_embeddings.Embed(entry.Title + "\n" + entry.Body));
                entry.StateHash = ContentHash.Of(entry);
                await db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* single item failure must not kill the worker */ }
        }
    }
}

/// <summary>Абстракция фабрики контекста для фоновых воркеров (DI-friendly).</summary>
public interface IDbContextFactoryLike
{
    AkashaDbContext Create();
}

public abstract class BackgroundWorkerBase
{
    public abstract Task RunAsync(CancellationToken ct);
}

/// <summary>Хеш содержимого записи (для state_hash и конфликтов синхронизации).</summary>
public static class ContentHash
{
    public static string Of(Entry e)
    {
        var node = new JsonObject
        {
            ["title"] = e.Title,
            ["body"] = e.Body,
            ["kind"] = e.Kind,
            ["tags"] = new JsonArray(e.Tags.Select(t => JsonValue.Create(t)).ToArray()),
            ["project_id"] = e.ProjectId,
            ["source_url"] = e.SourceUrl,
        };
        var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
