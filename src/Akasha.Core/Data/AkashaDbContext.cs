using System.Text.Json;
using Akasha.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Akasha.Core.Data;

/// <summary>
/// Единая DbContext для двух режимов: PostgreSQL (Npgsql) и SQLite (portable).
/// Векторы хранятся через float[] c кастомной конверсией (в Postgres — text-репрезентация vector,
/// pgvector-триггеры/индексы подключаются миграциями при наличии расширения).
/// </summary>
public class AkashaDbContext : DbContext
{
    public AkashaDbContext(DbContextOptions<AkashaDbContext> options) : base(options) { }

    public DbSet<Owner> Owners => Set<Owner>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Entry> Entries => Set<Entry>();
    public DbSet<EntryLink> EntryLinks => Set<EntryLink>();
    public DbSet<Proposal> Proposals => Set<Proposal>();
    public DbSet<Secret> Secrets => Set<Secret>();
    public DbSet<Script> Scripts => Set<Script>();
    public DbSet<ScriptCacheItem> ScriptCache => Set<ScriptCacheItem>();
    public DbSet<MetaEntry> Meta => Set<MetaEntry>();
    public DbSet<AuditLog> AuditLog => Set<AuditLog>();

    public bool IsSqlite => Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite";

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ─── DateTimeOffset: на SQLite храним как long (unix ms UTC) — иначе ORDER BY/сравнения не поддерживаются провайдером ──
        if (IsSqlite)
        {
            var dtoConv = new ValueConverter<DateTimeOffset, long>(
                d => d.ToUnixTimeMilliseconds(),
                l => DateTimeOffset.FromUnixTimeMilliseconds(l));
            var dtoNullableConv = new ValueConverter<DateTimeOffset?, long?>(
                d => d == null ? null : d.Value.ToUnixTimeMilliseconds(),
                l => l == null ? null : DateTimeOffset.FromUnixTimeMilliseconds(l.Value));

            foreach (var entityType in b.Model.GetEntityTypes())
                foreach (var prop in entityType.GetProperties())
                {
                    if (prop.ClrType == typeof(DateTimeOffset)) prop.SetValueConverter(dtoConv);
                    else if (prop.ClrType == typeof(DateTimeOffset?)) prop.SetValueConverter(dtoNullableConv);
                }
        }

        // ─── Общие конвертеры для JSON/массивов на SQLite ─────────────────────
        var jsonConv = new ValueConverter<JsonElement, string>(
            j => j.ValueKind == JsonValueKind.Undefined ? null! : j.GetRawText(),
            s => ParseJsonOrEmpty(s));
        var jsonNullableConv = new ValueConverter<JsonElement?, string?>(
            j => j == null || j.Value.ValueKind == JsonValueKind.Undefined ? null : j.Value.GetRawText(),
            s => ParseJsonOrNull(s));
        var strListConv = new ValueConverter<List<string>, string>(
            l => string.Join("\u001F", l),
            s => SplitStrList(s));
        var strListCmp = new ValueComparer<List<string>>(
            (a, c) => (a ?? new()).SequenceEqual(c ?? new()),
            v => v.Aggregate(0, (acc, x) => HashCode.Combine(acc, x.GetHashCode())),
            v => v.ToList());
        var embConv = new ValueConverter<float[]?, string?>(
            e => e == null ? null : string.Join(',', e.Select(f => f.ToString("R"))),
            s => ParseFloatArray(s));
        var embCmp = new ValueComparer<float[]>(
            (a, c) => (a ?? Array.Empty<float>()).SequenceEqual(c ?? Array.Empty<float>()),
            v => v.Aggregate(0, (acc, x) => HashCode.Combine(acc, x.GetHashCode())),
            v => (float[])v.Clone());

        // ─── Owner ────────────────────────────────────────────────────────────
        b.Entity<Owner>(e =>
        {
            e.ToTable("owners");
            e.HasIndex(x => x.Email).IsUnique();
        });

        // ─── Agent ────────────────────────────────────────────────────────────
        b.Entity<Agent>(e =>
        {
            e.ToTable("agents");
            e.Property(x => x.Scopes).HasConversion(strListConv).Metadata.SetValueComparer(strListCmp);
            e.HasIndex(x => x.KeyPrefix);
        });

        // ─── Project ──────────────────────────────────────────────────────────
        b.Entity<Project>(e =>
        {
            e.ToTable("projects");
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Stack).HasConversion(strListConv).Metadata.SetValueComparer(strListCmp);
            e.Property(x => x.Links).HasConversion(jsonNullableConv);
        });

        // ─── Entry ────────────────────────────────────────────────────────────
        b.Entity<Entry>(e =>
        {
            e.ToTable("entries");
            e.HasIndex(x => new { x.ProjectId, x.Kind, x.UpdatedAt });
            e.Property(x => x.Tags).HasConversion(strListConv).Metadata.SetValueComparer(strListCmp);
            // Те же данные в «сыром» виде — для переводимых LIKE-фильтров по тегам.
            e.Property(x => x.TagsRaw).HasColumnName("tags").HasColumnType("TEXT");
            e.Property(x => x.Metadata).HasConversion(jsonNullableConv);
            e.Property(x => x.Embedding).HasConversion(embConv).Metadata.SetValueComparer(embCmp);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId);
        });

        // ─── EntryLink ────────────────────────────────────────────────────────
        b.Entity<EntryLink>(e =>
        {
            e.ToTable("entry_links");
            e.HasKey(x => new { x.FromId, x.ToId, x.Relation });
            e.HasOne(x => x.From).WithMany().HasForeignKey(x => x.FromId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.To).WithMany().HasForeignKey(x => x.ToId).OnDelete(DeleteBehavior.Cascade);
        });

        // ─── Proposal ─────────────────────────────────────────────────────────
        b.Entity<Proposal>(e =>
        {
            e.ToTable("proposals");
            e.Property(x => x.Payload).HasConversion(jsonConv);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.AgentId, x.Status });
        });

        // ─── Secret ───────────────────────────────────────────────────────────
        b.Entity<Secret>(e =>
        {
            e.ToTable("secrets");
            if (!IsSqlite) e.Property(x => x.ValueEncrypted).HasColumnType("bytea");
        });

        // ─── Script ───────────────────────────────────────────────────────────
        b.Entity<Script>(e =>
        {
            e.ToTable("scripts");
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Query).HasConversion(jsonConv);
            e.Property(x => x.ParamsSchema).HasConversion(jsonNullableConv);
        });

        // ─── ScriptCache ──────────────────────────────────────────────────────
        b.Entity<ScriptCacheItem>(e =>
        {
            e.ToTable("script_cache");
            e.HasKey(x => x.CacheKey);
        });

        // ─── Meta ─────────────────────────────────────────────────────────────
        b.Entity<MetaEntry>(e =>
        {
            e.ToTable("meta");
            e.HasKey(x => x.Key);
        });

        // ─── AuditLog ─────────────────────────────────────────────────────────
        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_log");
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            if (IsSqlite) e.Property(x => x.Id).HasColumnType("INTEGER");
            e.HasIndex(x => x.At);
        });
    }

    private static JsonElement ParseJsonOrEmpty(string s) =>
        JsonDocument.Parse(string.IsNullOrEmpty(s) ? "{}" : s).RootElement;

    private static JsonElement? ParseJsonOrNull(string? s) =>
        s is null ? null : JsonDocument.Parse(s).RootElement;

    private static List<string> SplitStrList(string s) =>
        string.IsNullOrEmpty(s) ? new List<string>() : s.Split('\u001F').ToList();

    private static float[]? ParseFloatArray(string? s) =>
        string.IsNullOrEmpty(s) ? null : s.Split(',').Select(float.Parse).ToArray();

    /// <summary>Перехват SaveChanges: инкремент data_version + audit-безопасность bulk-операций (§3.3).</summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BumpDataVersionIfNeeded();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BumpDataVersionIfNeeded();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void BumpDataVersionIfNeeded()
    {
        var touched = ChangeTracker.Entries<Entry>().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<Project>().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            || ChangeTracker.Entries<EntryLink>().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        if (!touched) return;

        var meta = Meta.Local.FirstOrDefault(m => m.Key == "data_version")
                   ?? Meta.AsNoTracking().FirstOrDefault(m => m.Key == "data_version");
        if (meta is null)
        {
            Meta.Add(new MetaEntry { Key = "data_version", Value = 1 });
        }
        else
        {
            var tracked = ChangeTracker.Entries<MetaEntry>()
                .FirstOrDefault(en => en.Entity.Key == "data_version")?.Entity;
            if (tracked is not null) tracked.Value += 1;
            else
            {
                meta.Value += 1;
                Meta.Update(meta);
            }
        }
    }
}
