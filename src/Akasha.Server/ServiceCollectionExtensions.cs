using Akasha.Core.Data;
using Akasha.Core.Query;
using Akasha.Core.Services;
using Akasha.Server.Api;
using Akasha.Server.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Akasha.Server;

/// <summary>Обёртка BackgroundWorkerBase над hosted-сервисом .NET.</summary>
public class EmbeddingHostedService : BackgroundService
{
    private readonly Func<EmbeddingWorker> _factory;
    public EmbeddingHostedService(Func<EmbeddingWorker> factory) => _factory = factory;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => _factory().RunAsync(stoppingToken);
}

/// <summary>Фоновая генерация эмбеддингов из очереди (singleton worker со своей фабрикой контекста).</summary>
public class EmbeddingWorkerService : BackgroundService
{
    private readonly EmbeddingQueue _queue;
    private readonly IEmbeddingService _embeddings;
    private readonly IDbContextFactoryLike _dbFactory;

    public EmbeddingWorkerService(EmbeddingQueue queue, IEmbeddingService embeddings, IDbContextFactoryLike dbFactory)
    {
        _queue = queue; _embeddings = embeddings; _dbFactory = dbFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var db = _dbFactory.Create();
                var entry = await db.Entries.FirstOrDefaultAsync(e => e.Id == id, stoppingToken);
                if (entry is null || entry.DeletedAt is not null) continue;
                entry.Embedding = new Vector(_embeddings.Embed(entry.Title + "\n" + entry.Body));
                entry.StateHash = ContentHash.Of(entry);
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* best-effort */ }
        }
    }
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Регистрация всех сервисов Akasha. Используется и Server (PostgreSQL), и Portable (SQLite).
    /// </summary>
    public static IServiceCollection AddAkasha(this IServiceCollection services, IConfiguration cfg, bool portable)
    {
        services.AddSingleton<AppState>();
        var conn = portable
            ? cfg["AKASHA_SQLITE_PATH"] ?? "data/akasha.db"
            : cfg.GetConnectionString("Akasha") ?? cfg["AKASHA_DATABASE_URL"] ?? "Host=localhost;Database=akasha;Username=akasha;Password=akasha";
        var useSqlite = portable || conn.StartsWith("DataSource=", StringComparison.OrdinalIgnoreCase)
                        || conn.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
                        || conn.EndsWith(".db");

        var cs = useSqlite && !conn.Contains('=') ? $"Data Source={conn}" : conn;

        services.AddScoped<AkashaDbContext>(sp =>
        {
            var opts = new DbContextOptionsBuilder<AkashaDbContext>();
            if (useSqlite) opts.UseSqlite(cs);
            else opts.UseNpgsql(cs, o => o.UseVector());
            return new AkashaDbContext(opts.Options);
        });

        services.AddSingleton<IDbContextFactoryLike>(new DbContextFactory(cs, useSqlite));
        // portable-режим включает localhost-owner в AuthMiddleware (актуально и для Akasha.Portable host'а)
        if (portable)
            services.AddSingleton(new AuthMiddlewareOptions(true));
        services.AddSingleton<IEmbeddingService>(sp =>
        {
            // ONNX/BGE-Micro-V2 — opt-in через AKASHA_ONNX_MODEL; иначе детерминированный fallback.
            var model = cfg["AKASHA_ONNX_MODEL"] ?? Environment.GetEnvironmentVariable("AKASHA_ONNX_MODEL");
            if (string.IsNullOrWhiteSpace(model)) return new HashedNgramEmbeddings();

            var vocab = cfg["AKASHA_ONNX_VOCAB"] ?? Environment.GetEnvironmentVariable("AKASHA_ONNX_VOCAB")
                        ?? Path.Combine(Path.GetDirectoryName(model) ?? ".", "vocab.txt");
            var maxLenRaw = cfg["AKASHA_ONNX_MAXLEN"] ?? Environment.GetEnvironmentVariable("AKASHA_ONNX_MAXLEN");
            var maxLen = int.TryParse(maxLenRaw, out var ml) ? ml : 256;
            try
            {
                return new OnnxEmbeddingService(model, vocab, maxLen);
            }
            catch (Exception ex)
            {
                sp.GetService<ILoggerFactory>()?.CreateLogger("Akasha.Embeddings")
                    .LogWarning(ex, "ONNX-эмбеддинги не загрузились ({Model}); fallback на hashed-ngram.", model);
                return new HashedNgramEmbeddings();
            }
        });
        services.AddSingleton<EmbeddingQueue>();
        services.AddSingleton<QueryEngine>();
        services.AddSingleton<RateLimiter>();
        services.AddSingleton<SyncClient>();
        // FusionCache: L1-кэш in-memory (инвалидация по data_version в ключе + TTL скрипта).
        services.AddFusionCache();
        services.AddSingleton<Akasha.Server.Ai.QueryAssistService>();

        services.AddScoped<AuditService>();
        services.AddScoped<MetaService>();
        services.AddScoped<SearchService>();
        services.AddScoped<ProposalService>();
        services.AddScoped<ScriptService>();
        services.AddScoped<SyncService>();
        services.AddScoped<DiffService>();
        services.AddScoped<ExportService>();
        services.AddScoped<RecordsService>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddScoped<Akasha.Server.Mcp.McpService>();
        // Principal из Items["principal"], заполненного AuthMiddleware (нужен McpService)
        services.AddHttpContextAccessor();
        services.AddScoped(sp =>
        {
            var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
            if (http is not null && http.Items["principal"] is Akasha.Server.Auth.Principal p)
                return p;
            // portable без HTTP-контекста (stdio-MCP / CLI) — локальный владелец;
            // server без контекста — fail-closed.
            return portable
                ? Akasha.Server.Auth.Principal.Owner()
                : Akasha.Server.Auth.Principal.Anonymous();
        });

        // фоновый worker генерации эмбеддингов
        services.AddHostedService<EmbeddingWorkerService>();

        return services;
    }

    /// <summary>Создание схемы БД при старте (EnsureCreated — v1 без EF-миграций; pgvector включается отдельным шагом).</summary>
    public static async Task PrepareDatabaseAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AkashaDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<EmbeddingHostedService>>();
        if (db.IsSqlite)
        {
            await db.Database.EnsureCreatedAsync(ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        }
        else
        {
            try
            {
                // pgvector: расширение создаём ДО EnsureCreated, т.к. колонка Embedding имеет тип vector(384).
                await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector;", ct);
                await db.Database.EnsureCreatedAsync(ct);

                // EnsureCreated не мигрирует существующую схему. Если Embedding создан старой версией
                // как text — конвертируем в vector(384); эмбеддинги пересоздаст фоновый worker.
                var migrated = await MigrateEmbeddingColumnAsync(db, ct);

                // ivfflat-индекс для векторного поиска (best-effort).
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE INDEX IF NOT EXISTS ix_entries_embedding ON entries USING ivfflat (\"Embedding\" vector_cosine_ops) WITH (lists = 100);", ct);

                if (migrated) await ReenqueueMissingEmbeddingsAsync(sp, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "EnsureCreated / pgvector failed — install pgvector and run migrations manually (docker-compose initializes pgvector image).");
            }
        }

        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var (email, password) = await auth.EnsureOwnerAsync(ct);
        if (!string.IsNullOrEmpty(password))
            Console.WriteLine($"[akasha] owner created: {email} / {password}  (change via AKASHA_OWNER_PASSWORD env)");
    }

    /// <summary>Старая схема (колонка Embedding как text) → vector(384). true, если сконвертировали.</summary>
    private static async Task<bool> MigrateEmbeddingColumnAsync(AkashaDbContext db, CancellationToken ct)
    {
        var dataType = await db.Database.SqlQueryRaw<string>(
                "SELECT data_type AS \"Value\" FROM information_schema.columns WHERE table_name = 'entries' AND column_name = 'Embedding'")
            .FirstOrDefaultAsync(ct);
        if (!string.Equals(dataType, "text", StringComparison.OrdinalIgnoreCase))
            return false;

        await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE entries DROP COLUMN ""Embedding"";", ct);
        await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE entries ADD COLUMN ""Embedding"" vector(384);", ct);
        return true;
    }

    /// <summary>После конвертации колонки — ставим в очередь записи без эмбеддинга (worker пересчитает).</summary>
    private static async Task ReenqueueMissingEmbeddingsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var queue = sp.GetService<EmbeddingQueue>();
        if (queue is null) return;
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AkashaDbContext>();
        var ids = await db.Entries.Where(e => e.Embedding == null).Select(e => e.Id).ToListAsync(ct);
        foreach (var id in ids) queue.Enqueue(id);
    }
}
