using Akasha.Core.Query;
using Akasha.Core.Services;
using Xunit;

namespace Akasha.Tests;

public class QueryEngineTests : IDisposable
{
    private readonly TempDb _t = new();
    private readonly QueryEngine _engine = new();

    public void Dispose() => _t.Dispose();

    private async Task<QueryResult> Run(string queryJson, string? paramsJson = null)
    {
        var q = Json.Obj(queryJson);
        var p = paramsJson is null ? (System.Text.Json.JsonElement?)null : Json.Obj(paramsJson);
        return await _engine.ExecuteAsync(_t.Db, q, p);
    }

    [Fact]
    public async Task Select_All_Returns_Whitelisted_Fields()
    {
        var proj = _t.SeedProject("alpha", "Alpha");
        _t.SeedEntry(proj, "First note", "hello world", tags: new() { "x" });

        var r = await Run("""{"from":"entries","select":["id","title","kind","tags","project_id"]}""");
        Assert.Single(r.Rows);
        var row = r.Rows[0];
        Assert.Equal("First note", row["title"]);
        Assert.Equal("note", row["kind"]);
        Assert.Equal(proj.Id, row["project_id"]);
    }

    [Fact]
    public async Task Where_Kind_Filter_Works()
    {
        var p = _t.SeedProject();
        _t.SeedEntry(p, "a", kind: "note");
        _t.SeedEntry(p, "b", kind: "decision");

        var r = await Run("""{"from":"entries","where":{"kind":"decision"},"select":["title"]}""");
        Assert.Single(r.Rows);
        Assert.Equal("b", r.Rows[0]["title"]);
    }

    [Fact]
    public async Task Where_Title_Contains_Operator()
    {
        _t.SeedEntry(title: "PostgreSQL tuning", body: "…");
        _t.SeedEntry(title: "Redis cache", body: "…");

        var r = await Run("""{"from":"entries","where":{"title":{"$like":"Postgres"}},"select":["title"]}""");
        Assert.Single(r.Rows);
    }

    [Fact]
    public async Task Where_Tags_Contains_Any_All()
    {
        _t.SeedEntry(title: "A", tags: new() { "db", "perf" });
        _t.SeedEntry(title: "B", tags: new() { "db" });

        var any = await Run("""{"from":"entries","where":{"tags":{"$contains_any":["perf","ui"]}},"select":["title"]}""");
        Assert.Single(any.Rows);
        Assert.Equal("A", any.Rows[0]["title"]);

        var all = await Run("""{"from":"entries","where":{"tags":{"$contains_all":["db","perf"]}},"select":["title"]}""");
        Assert.Single(all.Rows);
    }

    [Fact]
    public async Task Params_Are_Parameterized_Not_Concatenated()
    {
        _t.SeedEntry(title: "safe");
        // попытка инъекции через значение параметра — должна трактоваться как литерал
        var r = await Run(
            """{"from":"entries","where":{"title":{"$eq":{"$param":"t"}}},"select":["title"]}""",
            """{"t": "' OR 1=1 --"}""");
        Assert.Empty(r.Rows);
    }

    [Fact]
    public async Task Unknown_Table_Rejected()
    {
        await Assert.ThrowsAsync<QueryEngine.QueryException>(
            () => Run("""{"from":"secrets"}"""));
    }

    [Fact]
    public async Task Unknown_Where_Field_Rejected()
    {
        await Assert.ThrowsAsync<QueryEngine.QueryException>(
            () => Run("""{"from":"entries","where":{"password":"1"}}"""));
    }

    [Fact]
    public async Task Limit_Clamped_To_HardLimit()
    {
        for (int i = 0; i < 3; i++) _t.SeedEntry(title: $"n{i}");
        var r = await Run("""{"from":"entries","limit":99999,"select":["id"]}""");
        Assert.True(QueryEngine.HardLimit >= r.Rows.Count);
        Assert.Equal(3, r.Rows.Count); // все помещаются, но не падаем
    }

    [Fact]
    public async Task Order_By_Title_Desc_And_Offset()
    {
        _t.SeedEntry(title: "A");
        _t.SeedEntry(title: "B");
        _t.SeedEntry(title: "C");
        var r = await Run("""{"from":"entries","order_by":[{"field":"title","dir":"desc"}],"limit":2,"offset":1,"select":["title"]}""");
        Assert.Equal(new[] { "B", "A" }, r.Rows.Select(x => x["title"]).ToArray());
    }

    [Fact]
    public async Task Projects_Table_Query_Works()
    {
        _t.SeedProject("one", "One");
        _t.SeedProject("two", "Two");
        var r = await Run("""{"from":"projects","where":{"status":"active"},"select":["slug","name"]}""");
        Assert.Equal(2, r.Rows.Count);
    }

    [Fact]
    public async Task Entry_Links_Table_Query_Works()
    {
        var a = _t.SeedEntry(title: "A");
        var b = _t.SeedEntry(title: "B");
        _t.Db.EntryLinks.Add(new Akasha.Core.Models.EntryLink { FromId = a.Id, ToId = b.Id, Relation = "mentions" });
        _t.Db.SaveChanges();

        var r = await Run("""{"from":"entry_links","select":["from_id","to_id","relation"]}""");
        Assert.Single(r.Rows);
        Assert.Equal("mentions", r.Rows[0]["relation"]);
    }

    [Fact]
    public void ValidateQueryShape_Rejects_Bad_From()
    {
        Assert.Throws<QueryEngine.QueryException>(
            () => _engine.ValidateQueryShape(Json.Obj("""{"from":"audit_log"}""")));
        _engine.ValidateQueryShape(Json.Obj("""{"from":"entries","where":{"kind":"note"}}""")); // ok
    }

    [Fact]
    public void SchemaJson_Is_Valid_Json_With_Definitions()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(QueryEngine.GetSchemaJson());
        Assert.True(doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object);
    }

    [Fact]
    public async Task JmesTransform_Applies_On_Rows()
    {
        _t.SeedEntry(title: "X");
        var rows = (await Run("""{"from":"entries","select":["title"]}""")).Rows;
        var result = JmesTransform.Apply(rows, "[].title");
        Assert.Equal("X", ((System.Text.Json.JsonElement)result!).EnumerateArray().First().GetString());
    }
}
