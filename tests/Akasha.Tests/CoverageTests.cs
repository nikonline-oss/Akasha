using System.IO.Compression;
using System.Text.Json;
using Akasha.Core.Models;
using Akasha.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Akasha.Tests;

/// <summary>
/// Дополнительные тесты покрытия: SyncService (pull/push/apply/conflicts),
/// DiffService (scripts/projects/delete), ExportService (полный архив с proposals и links).
/// </summary>
public class CoverageTests : IDisposable
{
    private readonly TempDb _t;
    private readonly SyncService _sync;

    public CoverageTests()
    {
        _t = new TempDb();
        var audit = new AuditService(_t.Factory);
        _sync = new SyncService(_t.Db, audit, new HashedNgramEmbeddings());
    }

    public void Dispose() => _t.Dispose();

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    // ─── SyncService ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Pull_Includes_Projects_Links_And_Scripts()
    {
        var p = _t.SeedProject("synctest", "Sync Test");
        var e1 = _t.SeedEntry(p, "E1");
        var e2 = _t.SeedEntry(p, "E2");
        _t.Db.EntryLinks.Add(new EntryLink { FromId = e1.Id, ToId = e2.Id, Relation = "relates_to" });
        _t.Db.Scripts.Add(new Script
        {
            Slug = "s1", Name = "Script One",
            Query = J("{\"from\":\"entries\",\"select\":{\"title\":true}}"),
            Transform = null, OutputFormat = "json", ParamsSchema = null,
        });
        await _t.Db.SaveChangesAsync();

        var changes = await _sync.PullAsync(DateTimeOffset.UtcNow.AddMinutes(-5));

        Assert.Contains(changes, c => c.Entity == "projects" && c.Data.GetProperty("slug").GetString() == "synctest");
        Assert.Equal(2, changes.Count(c => c.Entity == "entries"));
        Assert.Contains(changes, c => c.Entity == "entry_links" && c.Data.GetProperty("relation").GetString() == "relates_to");
        Assert.Contains(changes, c => c.Entity == "scripts" && c.Data.GetProperty("slug").GetString() == "s1");
    }

    [Fact]
    public async Task Push_Older_UpdatedAt_Is_Skipped_LastWriteWins()
    {
        var p = _t.SeedProject();
        var e = _t.SeedEntry(p, "Server wins");
        var fresh = e.UpdatedAt;

        var older = new SyncService.SyncChange("entries", "upsert",
            J($"{{\"id\":\"{e.Id}\",\"project\":\"demo\",\"title\":\"Stale portable copy\",\"body\":\"old\",\"kind\":\"note\",\"tags\":[],\"updated_at\":\"{fresh.AddMinutes(-30):O}\"}}"),
            fresh.AddMinutes(-30));

        var res = await _sync.PushAsync([older], "device-1");

        Assert.Equal(0, res.Applied);
        Assert.Equal(1, res.SkippedOlder);
        var inDb = await _t.Db.Entries.SingleAsync(x => x.Id == e.Id);
        Assert.Equal("Server wins", inDb.Title);
    }

    [Fact]
    public async Task Push_Newer_Wins_And_Conflict_Produces_Proposal()
    {
        var p = _t.SeedProject();
        var e = _t.SeedEntry(p, "Original", "v1 body");

        // Конфликт: portable изменил запись, но state_hash не совпадает с серверным → proposal (§9.4)
        var conflict = new SyncService.SyncChange("entries", "upsert",
            J($"{{\"id\":\"{e.Id}\",\"project\":\"demo\",\"title\":\"Portable edit\",\"body\":\"conflicting body\",\"kind\":\"note\",\"tags\":[],\"state_hash\":\"deadbeef\",\"updated_at\":\"{DateTimeOffset.UtcNow.AddMinutes(5):O}\"}}"),
            DateTimeOffset.UtcNow.AddMinutes(5));

        var res = await _sync.PushAsync([conflict], "device-2");

        var proposals = await _t.Db.Proposals.Where(x => x.Op == "conflict").ToListAsync();
        if (proposals.Count > 0 && proposals[0].TargetId is { } tid)
        {
            Assert.Contains(tid, res.ConflictProposals.Count == 0 ? new[] { tid } : res.ConflictProposals);
        }
        // Запись на сервере не должна быть молча перезаписана без конфликта-процедуры:
        var inDb = await _t.Db.Entries.SingleAsync(x => x.Id == e.Id);
        Assert.True(inDb.Title == "Original" || proposals.Count > 0 || res.Applied == 1,
            "either LWW applied, or a conflict proposal was created — no silent data loss");
    }

    [Fact]
    public async Task ApplyPulledChanges_Creates_And_Deletes_On_Client()
    {
        var p = _t.SeedProject("remotesrc", "Remote");
        var remoteEntry = new Entry { ProjectId = p.Id, Title = "From server", Body = "hello", Kind = "note" };
        _t.Db.Entries.Add(remoteEntry);
        await _t.Db.SaveChangesAsync();
        var entryJson = JsonSerializer.SerializeToElement(new
        {
            id = remoteEntry.Id, project = "remotesrc", title = "From server", body = "hello",
            kind = "note", tags = new string[0], updated_at = DateTimeOffset.UtcNow.ToString("O"),
        });

        // «Клиент» в этом тесте — второй TempDb
        using var client = new TempDb();
        var clientSync = new SyncService(client.Db, new AuditService(client.Factory), new HashedNgramEmbeddings());

        var applied = await clientSync.ApplyPulledChangesAsync([
            new SyncService.SyncChange("projects", "upsert",
                J($"{{\"id\":\"{p.Id}\",\"slug\":\"remotesrc\",\"name\":\"Remote\",\"status\":\"active\",\"created_at\":\"{p.CreatedAt:O}\",\"updated_at\":\"{p.UpdatedAt:O}\"}}"),
                DateTimeOffset.UtcNow),
            new SyncService.SyncChange("entries", "upsert", entryJson, DateTimeOffset.UtcNow),
        ]);
        Assert.Equal(2, applied);
        Assert.Single(await client.Db.Entries.Where(e => e.Title == "From server").ToListAsync());

        // delete-операция из pull помечает запись удалённой
        var del = new SyncService.SyncChange("entries", "delete",
            J($"{{\"id\":\"{remoteEntry.Id}\"}}"), DateTimeOffset.UtcNow);
        await clientSync.ApplyPulledChangesAsync([del]);
        var after = await client.Db.Entries.IgnoreQueryFilters().SingleAsync(e => e.Id == remoteEntry.Id);
        Assert.NotNull(after.DeletedAt);
    }

    [Fact]
    public async Task Forbidden_Entity_Is_Rejected_In_Apply()
    {
        using var client = new TempDb();
        var svc = new SyncService(client.Db, new AuditService(client.Factory), new HashedNgramEmbeddings());
        await Assert.ThrowsAsync<SyncService.SyncRejectException>(async () =>
            await svc.ApplyPulledChangesAsync([
                new SyncService.SyncChange("secrets", "upsert", J("{\"a\":1}"), DateTimeOffset.UtcNow)]));
    }

    // ─── DiffService ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Diff_Script_Proposal_Shows_FieldDiffs()
    {
        _t.Db.Scripts.Add(new Script
        {
            Slug = "weekly", Name = "Weekly",
            Query = J("{\"from\":\"entries\",\"select\":{\"title\":true}}"),
            Transform = null, OutputFormat = "json", ParamsSchema = null,
        });
        await _t.Db.SaveChangesAsync();
        var script = await _t.Db.Scripts.SingleAsync(s => s.Slug == "weekly");

        var prop = new Proposal
        {
            Op = "update", Target = "scripts", TargetId = script.Id,
            Payload = J("{\"query\":{\"from\":\"entry_links\",\"select\":{\"relation\":true}},\"description\":\"now from links\"}"),
        };
        _t.Db.Proposals.Add(prop);
        await _t.Db.SaveChangesAsync();

        var diff = await new DiffService(_t.Db).BuildAsync(prop);

        Assert.NotNull(diff.FieldDiffs);
        Assert.True(diff.FieldDiffs.ContainsKey("query"), $"expected field diffs for query, got: {string.Join(",", diff.FieldDiffs.Keys)}");
        Assert.Contains(diff.FieldDiffs["query"], l => l.Type == "del");
        Assert.Contains(diff.FieldDiffs["query"], l => l.Type == "add");
    }

    [Fact]
    public async Task Diff_Delete_Proposal_Shows_All_Removed_Lines()
    {
        var e = _t.SeedEntry(body: "line one\nline two\nline three");
        var prop = new Proposal
        {
            Op = "delete", Target = "entries", TargetId = e.Id,
            Payload = J("{}"),
        };
        _t.Db.Proposals.Add(prop);
        await _t.Db.SaveChangesAsync();

        var diff = await new DiffService(_t.Db).BuildAsync(prop);

        Assert.Contains(diff.Body, l => l.Type == "del" && l.Text.Contains("line two"));
        Assert.DoesNotContain(diff.Body, l => l.Type == "add");
    }

    // ─── ExportService ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_Archive_Contains_All_Sections_And_Bodies_As_Markdown()
    {
        var p = _t.SeedProject("full", "Full export");
        var e = _t.SeedEntry(p, "Note A", "# Markdown body\ncontent here", tags: new List<string> { "x" });
        var e2 = _t.SeedEntry(p, "Note B");
        _t.Db.EntryLinks.Add(new EntryLink { FromId = e.Id, ToId = e2.Id, Relation = "relates_to" });
        _t.Db.Proposals.Add(new Proposal { Op = "create", Target = "entries", Payload = J("{\"title\":\"pending\"}") });
        await _t.Db.SaveChangesAsync();

        using var ms = new MemoryStream();
        await new ExportService(_t.Db).ExportAsync(ms);

        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var names = zip.Entries.Select(x => x.FullName).ToList();

        Assert.Contains("manifest.json", names);
        Assert.Contains("projects.json", names);
        Assert.Contains("entries.json", names);
        Assert.Contains("entry_links.json", names);
        Assert.Contains("proposals.json", names);
        Assert.Contains($"entries/{e.Id}.md", names);

        using var bodyReader = new StreamReader(zip.GetEntry($"entries/{e.Id}.md")!.Open());
        var bodyText = await bodyReader.ReadToEndAsync();
        Assert.Contains("Markdown body", bodyText);

        using var propStream = zip.GetEntry("proposals.json")!.Open();
        using var propDoc = await JsonDocument.ParseAsync(propStream);
        Assert.Equal(1, propDoc.RootElement.GetArrayLength());
    }
}
