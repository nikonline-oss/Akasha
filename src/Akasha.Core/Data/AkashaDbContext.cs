using System.Globalization;
using System.Text.Json;
using Akasha.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;

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
    public DbSet<ContextNote> ContextNotes => Set<ContextNote>();

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

        // pgvector: сообщаем Postgres о расширении vector (тип-маппинг включается UseVector в Npgsql-опциях).
        if (!IsSqlite)
            b.HasPostgresExtension("vector");

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
        var embConv = new ValueConverter<Vector?, string?>(
            v => v == null ? null : string.Join(',', v.ToArray().Select(f => f.ToString("R", CultureInfo.InvariantCulture))),
            s => string.IsNullOrEmpty(s) ? null : new Vector(s.Split(',', StringSplitOptions.None).Select(x => float.Parse(x, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture)).ToArray()));

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
            // Колонка tags — единая; List<string> конвертируется в TEXT (\u001F-разделитель).
            e.Property(x => x.Tags).HasColumnName("tags").HasConversion(strListConv).Metadata.SetValueComparer(strListCmp);
            // TagsRaw — «сырая» копия той же строки тегов в отдельной колонке tags_raw.
            // Синхронизируется из Tags в AkashaDbContext.SaveChanges* (см. SyncTagsRaw).
            // Нужна для переводимых LIKE-фильтров по тегам (QueryEngine.TagPredicate).
            e.Property(x => x.TagsRaw).HasColumnName("tags_raw").HasColumnType("TEXT");
            e.Property(x => x.Metadata).HasConversion(jsonNullableConv);
            if (IsSqlite)
                e.Property(x => x.Embedding).HasConversion(embConv);
            else
                e.Property(x => x.Embedding).HasColumnType("vector(384)");
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId);
        });

        // ─── ContextNote (хранилище контекста агентов, без апрувов) ────────────
        b.Entity<ContextNote>(e =>
        {
            e.ToTable("context_notes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Tags).HasColumnName("tags").HasConversion(strListConv).Metadata.SetValueComparer(strListCmp);
            e.HasIndex(x => new { x.ProjectId, x.UpdatedAt });
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

    /// <summary>Перехват SaveChanges: инкремент data_version + audit-безопасность bulk-операций (§3.3).</summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        SyncTagsRaw();
        BumpDataVersionIfNeeded();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        SyncTagsRaw();
        BumpDataVersionIfNeeded();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Поддерживает TagsRaw (колонка tags_raw) в синхронизации с Tags — для переводимых
    /// LIKE-фильтров по тегам. Формат: \u001Ftag1\u001Ftag2\u001F (тот же разделитель, что и strListConv).
    /// </summary>
    private void SyncTagsRaw()
    {
        foreach (var ent in ChangeTracker.Entries<Entry>())
        {
            if (ent.State is EntityState.Added or EntityState.Modified)
                ent.Entity.TagsRaw = "\u001F" + string.Join("\u001F", ent.Entity.Tags ?? new List<string>()) + "\u001F";
        }
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
