using Akasha.Core.Data;
using Akasha.Core.Query;
using Akasha.Core.Services;
using Akasha.Server.Api;
using Akasha.Server.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
                entry.Embedding = _embeddings.Embed(entry.Title + "\n" + entry.Body);
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
            else opts.UseNpgsql(cs);
            return new AkashaDbContext(opts.Options);
        });

        services.AddSingleton<IDbContextFactoryLike>(new DbContextFactory(cs, useSqlite));
        // portable-режим включает localhost-owner в AuthMiddleware (актуально и для Akasha.Portable host'а)
        if (portable)
            services.AddSingleton(new AuthMiddlewareOptions(true));
        services.AddSingleton<IEmbeddingService, HashedNgramEmbeddings>();
        services.AddSingleton<EmbeddingQueue>();
        services.AddSingleton<QueryEngine>();
        services.AddSingleton<RateLimiter>();

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
            return http is not null && http.Items["principal"] is Akasha.Server.Auth.Principal p
                ? p
                : Akasha.Server.Auth.Principal.Owner();
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
                await db.Database.EnsureCreatedAsync(ct);
                // pgvector: extension (best-effort, требует прав; в docker-образе pgvector предуставлен)
                await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector;", ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "EnsureCreated failed — run migrations manually (docker-compose initializes pgvector image).");
            }
        }

        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var (email, password) = await auth.EnsureOwnerAsync(ct);
        if (!string.IsNullOrEmpty(password))
            Console.WriteLine($"[akasha] owner created: {email} / {password}  (change via AKASHA_OWNER_PASSWORD env)");
    }
}
