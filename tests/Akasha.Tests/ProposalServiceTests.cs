using Akasha.Core.Models;
using Akasha.Core.Query;
using Akasha.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Akasha.Tests;

public class ProposalServiceTests : IDisposable
{
    private readonly TempDb _t = new();
    private readonly ProposalService _svc;

    public ProposalServiceTests()
    {
        var audit = new AuditService(_t.Factory);
        var emb = new HashedNgramEmbeddings();
        _svc = new ProposalService(_t.Db, audit, emb);
    }

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task CreateEntryProposal_Apply_Creates_Entry()
    {
        var proj = _t.SeedProject("demo");
        var p = await _svc.CreateAsync(null, "create", "entries", null,
            Json.Obj($$"""{"title":"From agent","body":"content","kind":"note","project":"demo"}"""), null);
        Assert.Equal(ProposalStatus.Pending, p.Status);

        var approved = await _svc.ApproveAsync(p.Id);
        Assert.Equal(ProposalStatus.Applied, approved.Status);

        var entry = await _t.Db.Entries.SingleAsync(e => e.Title == "From agent");
        Assert.Equal(proj.Id, entry.ProjectId);
        Assert.NotNull(entry.Embedding);
        Assert.NotNull(entry.StateHash);
    }

    [Fact]
    public async Task UpdateProposal_With_Stale_BaseVersion_Conflicts_At_Approve()
    {
        var e = _t.SeedEntry(title: "orig");
        // proposal создан с base_version 1 — ок
        var p = await _svc.CreateAsync(null, "update", "entries", e.Id,
            Json.Obj("""{"title":"new"}"""), 1);
        // данные ушли вперёд
        e.Version = 5;
        _t.Db.SaveChanges();

        await Assert.ThrowsAsync<ProposalService.ConflictException>(() => _svc.ApproveAsync(p.Id));
    }

    [Fact]
    public async Task CreateProposal_Invalid_Op_Rejected()
    {
        await Assert.ThrowsAsync<ProposalService.ValidationException>(
            () => _svc.CreateAsync(null, "merge", "entries", null, Json.Obj("""{"title":"a","body":"b"}"""), null));
    }

    [Fact]
    public async Task CreateProposal_Entry_Without_Title_Rejected()
    {
        await Assert.ThrowsAsync<ProposalService.ValidationException>(
            () => _svc.CreateAsync(null, "create", "entries", null, Json.Obj("""{"body":"b"}"""), null));
    }

    [Fact]
    public async Task Reject_Requires_Comment_And_Sets_Status()
    {
        var p = await _svc.CreateAsync(null, "create", "entries", null,
            Json.Obj("""{"title":"t","body":"b"}"""), null);

        await Assert.ThrowsAsync<ProposalService.ValidationException>(
            () => _svc.RejectAsync(p.Id, ""));

        var rejected = await _svc.RejectAsync(p.Id, "not my style");
        Assert.Equal(ProposalStatus.Rejected, rejected.Status);
        Assert.Equal("not my style", rejected.ReviewComment);
    }

    [Fact]
    public async Task Approve_Twice_Fails()
    {
        var p = await _svc.CreateAsync(null, "create", "entries", null,
            Json.Obj("""{"title":"t2","body":"b"}"""), null);
        await _svc.ApproveAsync(p.Id);
        await Assert.ThrowsAsync<ProposalService.ValidationException>(() => _svc.ApproveAsync(p.Id));
    }

    [Fact]
    public async Task DeleteProposal_SoftDeletes_Entry()
    {
        var e = _t.SeedEntry(title: "to delete");
        var p = await _svc.CreateAsync(null, "delete", "entries", e.Id, Json.Obj("{}"), e.Version);
        await _svc.ApproveAsync(p.Id);
        var reloaded = await _t.Db.Entries.FirstAsync(x => x.Id == e.Id);
        Assert.NotNull(reloaded.DeletedAt);
    }

    [Fact]
    public async Task ScriptProposal_Validates_Query_From()
    {
        await Assert.ThrowsAsync<ProposalService.ValidationException>(
            () => _svc.CreateAsync(null, "create", "scripts", null,
                Json.Obj("""{"slug":"s","name":"S","query":{"from":"secrets"},"output_format":"json"}"""), null));

        var p = await _svc.CreateAsync(null, "create", "scripts", null,
            Json.Obj("""{"slug":"recent","name":"Recent","query":{"from":"entries","limit":5},"output_format":"table","cache_ttl":60}"""), null);
        await _svc.ApproveAsync(p.Id);
        Assert.True(await _t.Db.Scripts.AnyAsync(s => s.Slug == "recent"));
    }

    [Fact]
    public async Task ProjectProposal_Create_And_Update_Status()
    {
        var p = await _svc.CreateAsync(null, "create", "projects", null,
            Json.Obj("""{"name":"New Project","description":"d","stack":["dotnet"]}"""), null);
        await _svc.ApproveAsync(p.Id);
        var proj = await _t.Db.Projects.SingleAsync(x => x.Name == "New Project");
        Assert.Equal("new-project", proj.Slug);

        var u = await _svc.CreateAsync(null, "update", "projects", proj.Id,
            Json.Obj("""{"status":"archived"}"""), null);
        await _svc.ApproveAsync(u.Id);
        var reloaded = await _t.Db.Projects.FirstAsync(x => x.Id == proj.Id);
        Assert.Equal("archived", reloaded.Status);
    }

    [Fact]
    public async Task DiffService_Builds_LineDiff_For_Update()
    {
        var e = _t.SeedEntry(title: "Doc", body: "line1\nline2\nline3");
        var p = await _svc.CreateAsync(null, "update", "entries", e.Id,
            Json.Obj("""{"body":"line1\nchanged\nline3"}"""), e.Version);

        var diff = new DiffService(_t.Db);
        var d = await diff.BuildAsync(p);
        Assert.Contains(d.Body, l => l.Type == "add" && l.Text == "changed");
        Assert.Contains(d.Body, l => l.Type == "del" && l.Text == "line2");
        Assert.False(string.IsNullOrEmpty(d.Summary));
    }
}
