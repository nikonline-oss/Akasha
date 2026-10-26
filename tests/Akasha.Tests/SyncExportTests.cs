using System.IO.Compression;
using System.Text.Json;
using Akasha.Core.Models;
using Akasha.Core.Query;
using Akasha.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Akasha.Tests;

public class SyncServiceTests : IDisposable
{
    private readonly TempDb _t = new();
    private readonly SyncService _svc;

    public SyncServiceTests()
    {
        var audit = new AuditService(_t.Factory);
        _svc = new SyncService(_t.Db, audit, new HashedNgramEmbeddings());
    }

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task Pull_Returns_Entries_Changed_After_Since()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-1);
        _t.SeedEntry(title: "new one");
        await Task.Delay(50);
        var since = DateTimeOffset.UtcNow;
        _t.SeedEntry(title: "newer one");

        var changes = await _svc.PullAsync(cutoff);
        Assert.Contains(changes, c => c.Entity == "entries");

        var onlyFresh = await _svc.PullAsync(since.AddSeconds(1));
        // "newer one" создан после since — должен попасть
        Assert.Contains(onlyFresh, c => c.Data.GetProperty("title").GetString() == "newer one");
    }

    [Fact]
    public async Task Push_Rejects_Forbidden_Entities()
    {
        foreach (var entity in SyncService.ForbiddenEntities)
        {
            await Assert.ThrowsAsync<SyncService.SyncRejectException>(() =>
                _svc.PushAsync(new List<SyncService.SyncChange>
                {
                    new(entity, "upsert", Json.Obj("{}"), DateTimeOffset.UtcNow)
                }, "device-1"));
        }
    }

    [Fact]
    public async Task Push_New_Entry_Is_Applied()
    {
        var id = Guid.NewGuid();
        var changes = new List<SyncService.SyncChange>
        {
            new("entries", "upsert", Json.Obj($$"""
                {"id":"{{id}}","title":"from device","body":"b","kind":"note","version":1}
                """), DateTimeOffset.UtcNow)
        };
        var r = await _svc.PushAsync(changes, "device-1");
        Assert.Equal(1, r.Applied);
        Assert.True(await _t.Db.Entries.AnyAsync(e => e.Id == id && e.Title == "from device"));
    }

    [Fact]
    public async Task Push_Older_Change_Is_Skipped_LastWriteWins()
    {
        var e = _t.SeedEntry(title: "server version");
        var newer = DateTimeOffset.UtcNow;
        var older = newer.AddDays(-2);

        var changes = new List<SyncService.SyncChange>
        {
            new("entries", "upsert", Json.Obj($$"""
                {"id":"{{e.Id}}","title":"stale device edit","body":"b","kind":"note","version":9,"updated_at":"{{older.ToString("O")}}"}
                """), older)
        };
        var r = await _svc.PushAsync(changes, "device-1");
        Assert.Equal(0, r.Applied);
        Assert.Equal(1, r.SkippedOlder);
        var reloaded = await _t.Db.Entries.FirstAsync(x => x.Id == e.Id);
        Assert.Equal("server version", reloaded.Title);
    }

    [Fact]
    public async Task ApplyPulledChanges_Creates_And_Rejects_Forbidden()
    {
        var id = Guid.NewGuid();
        var applied = await _svc.ApplyPulledChangesAsync(new List<SyncService.SyncChange>
        {
            new("entries", "upsert", Json.Obj($$"""
                {"id":"{{id}}","title":"pulled","body":"x","kind":"note","version":1}
                """), DateTimeOffset.UtcNow)
        });
        Assert.Equal(1, applied);
        Assert.True(await _t.Db.Entries.AnyAsync(e => e.Id == id));

        await Assert.ThrowsAsync<SyncService.SyncRejectException>(() =>
            _svc.ApplyPulledChangesAsync(new List<SyncService.SyncChange>
            {
                new("secrets", "upsert", Json.Obj("{}"), DateTimeOffset.UtcNow)
            }));
    }
}

public class ExportImportTests : IDisposable
{
    private readonly TempDb _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task Export_Then_Import_Roundtrip_Into_Fresh_Db()
    {
        var proj = _t.SeedProject("roundtrip", "Roundtrip");
        var e1 = _t.SeedEntry(proj, "Note one", "first body", tags: new() { "a", "b" });
        var e2 = _t.SeedEntry(proj, "Note two", "second body");
        _t.Db.EntryLinks.Add(new EntryLink { FromId = e1.Id, ToId = e2.Id, Relation = "mentions" });
        _t.Db.Scripts.Add(new Script
        {
            Slug = "s1", Name = "S1", Query = Json.Obj("""{"from":"entries"}"""), OutputFormat = "table"
        });
        _t.Db.SaveChanges();

        var ms = new MemoryStream();
        await new ExportService(_t.Db).ExportAsync(ms);
        ms.Position = 0;

        // валидность zip и наличие файлов открытого формата
        using (var check = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true))
        {
            var names = check.Entries.Select(x => x.FullName).ToList();
            Assert.Contains("projects.json", names);
            Assert.Contains("entries.json", names);
            Assert.Contains($"entries/{e1.Id}.md", names);
            Assert.Contains("manifest.json", names);
            Assert.DoesNotContain(names, n => n.StartsWith("secrets"));
        }

        ms.Position = 0;
        using var fresh = new TempDb();
        var imported = await new ExportService(fresh.Db).ImportAsync(ms);
        Assert.True(imported >= 4); // 1 project + 2 entries + 1 script (+links)

        var p = await fresh.Db.Projects.SingleAsync(x => x.Slug == "roundtrip");
        Assert.Equal("Roundtrip", p.Name);
        var n = await fresh.Db.Entries.SingleAsync(x => x.Title == "Note one");
        Assert.Equal("first body", n.Body);
        Assert.Equal(new[] { "a", "b" }, n.Tags);
        Assert.True(await fresh.Db.EntryLinks.AnyAsync(l => l.FromId == n.Id));
    }

    [Fact]
    public async Task Import_Is_Idempotent()
    {
        _t.SeedProject("idem");
        var ms = new MemoryStream();
        await new ExportService(_t.Db).ExportAsync(ms);
        ms.Position = 0;

        var first = await new ExportService(_t.Db).ImportAsync(ms);
        ms.Position = 0;
        var second = await new ExportService(_t.Db).ImportAsync(ms);
        Assert.Equal(0, second); // всё уже есть
        Assert.Equal(0, first);  // проект не дублируется (существует по slug)
        Assert.Equal(1, await _t.Db.Projects.CountAsync());
    }

    [Fact]
    public async Task Export_Without_Body_Links_Still_Has_Manifest()
    {
        var ms = new MemoryStream();
        await new ExportService(_t.Db).ExportAsync(ms);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        using var manifest = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        using var doc = JsonDocument.Parse(await manifest.ReadToEndAsync());
        Assert.True(doc.RootElement.TryGetProperty("format", out _));
    }
}
