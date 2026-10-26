using Akasha.Core.Data;
using Akasha.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Tests;

/// <summary>
/// SQLite in-memory контекст для интеграции-подобных тестов Core-сервисов.
/// Один общий SqliteConnection на fixture — БД живёт пока открыто соединение.
/// </summary>
public sealed class TempDb : IDisposable
{
    public SqliteConnection Conn { get; }
    public AkashaDbContext Db { get; }
    public SharedFactory Factory { get; }

    public TempDb()
    {
        // именованная in-memory БД (shared cache) — видна всем соединениям этой строки
        var name = $"akashatest{Guid.NewGuid():N}";
        Conn = new SqliteConnection($"Data Source=MemoryDb_{name};Mode=Memory;Cache=Shared");
        Conn.Open(); // держим открытым — иначе shared-cache БД уничтожается
        var opts = new DbContextOptionsBuilder<AkashaDbContext>().UseSqlite(Conn).Options;
        Db = new AkashaDbContext(opts);
        Db.Database.EnsureCreated();
        Factory = new SharedFactory(Conn);
    }

    public Project SeedProject(string slug = "demo", string name = "Demo")
    {
        var p = new Project { Slug = slug, Name = name };
        Db.Projects.Add(p);
        Db.SaveChanges();
        return p;
    }

    public Entry SeedEntry(Project? project = null, string title = "Note", string body = "Body text",
        string kind = "note", List<string>? tags = null)
    {
        var e = new Entry
        {
            ProjectId = project?.Id,
            Title = title,
            Body = body,
            Kind = kind,
            Tags = tags ?? new(),
        };
        Db.Entries.Add(e);
        Db.SaveChanges();
        return e;
    }

    public void Dispose()
    {
        Db.Dispose();
        Conn.Dispose();
    }
}

public static class Json
{
    public static System.Text.Json.JsonElement Obj(string raw)
        => System.Text.Json.JsonDocument.Parse(raw).RootElement.Clone();
}

/// <summary>Фабрика контекстов поверх того же SQLite-соединения (для AuditService/MetaService в тестах).</summary>
public sealed class SharedFactory : Akasha.Core.Services.IDbContextFactoryLike
{
    private readonly SqliteConnection _conn;
    public SharedFactory(SqliteConnection conn) => _conn = conn;

    public AkashaDbContext Create()
    {
        var opts = new DbContextOptionsBuilder<AkashaDbContext>().UseSqlite(_conn).Options;
        return new AkashaDbContext(opts);
    }
}
