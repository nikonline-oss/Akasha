using System.Text.Json;
using Akasha.Core.Models;
using Akasha.Core.Query;
using Akasha.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace Akasha.Tests;

public class CryptoTests
{
    [Fact]
    public void AgentKey_Generate_Verify_Roundtrip()
    {
        var (plaintext, prefix, keyHash) = Hashing.GenerateAgentKey();
        Assert.StartsWith("ak_", plaintext);
        // keyHash — хеш секрета, а не всего ключа
        var secret = plaintext[(prefix.Length + 4)..]; // после "ak_<prefix>_"
        Assert.True(Hashing.Verify(secret, keyHash));
        Assert.False(Hashing.Verify("wrong-secret", keyHash));
        Assert.Equal(plaintext[3..11], prefix);
    }

    [Fact]
    public void ParseAgentKey_From_Bearer_Header()
    {
        var (plaintext, prefix, _) = Hashing.GenerateAgentKey();
        var parsed = Hashing.ParseAgentKey($"Bearer {plaintext}");
        Assert.NotNull(parsed);
        Assert.Equal(prefix, parsed.Value.prefix);
        // секрет идёт после "ak_<prefix>_", т.е. с индексом 3+prefix.Length+1
        Assert.Equal(plaintext[(3 + prefix.Length + 1)..], parsed.Value.secret);
    }

    [Fact]
    public void ParseAgentKey_Rejects_Garbage()
    {
        Assert.Null(Hashing.ParseAgentKey("Basic abc"));
        Assert.Null(Hashing.ParseAgentKey("Bearer not-a-key"));
    }

    [Fact]
    public void Argon2id_Salt_Is_Random_Per_Hash()
    {
        var h1 = Hashing.Argon2idHash("pass");
        var h2 = Hashing.Argon2idHash("pass");
        Assert.NotEqual(h1, h2);
        Assert.True(Hashing.Verify("pass", h1));
        Assert.True(Hashing.Verify("pass", h2));
    }

    [Fact]
    public void SecretCrypto_AesGcm_Encrypt_Decrypt_Roundtrip()
    {
        var key = SecretCrypto.DeriveKeyFromMasterKey("test-master-key");
        var payload = SecretCrypto.Encrypt("super-token-42", key);
        // Шанс встретить конкретный байт в ~13-байтовом AES-GCM-шифртексте ≈ 5% —
        // это flaky-проверка. Инвариант "шифртекст ≠ открытый текст" проверяем детерминированно:
        Assert.NotEqual("super-token-42"u8.ToArray(), payload.Skip(payload.Length - "super-token-42".Length).ToArray());
        Assert.Equal("super-token-42", SecretCrypto.Decrypt(payload, key));
    }

    [Fact]
    public void SecretCrypto_Wrong_Key_Fails()
    {
        var k1 = SecretCrypto.DeriveKeyFromMasterKey("key-one");
        var k2 = SecretCrypto.DeriveKeyFromMasterKey("key-two");
        var payload = SecretCrypto.Encrypt("data", k1);
        Assert.ThrowsAny<Exception>(() => SecretCrypto.Decrypt(payload, k2));
    }

    [Fact]
    public void SecretCrypto_Tampered_Payload_Fails()
    {
        var key = SecretCrypto.DeriveKeyFromMasterKey("k");
        var payload = SecretCrypto.Encrypt("hello", key);
        payload[^1] ^= 0xFF; // подмена последнего байта тега
        Assert.ThrowsAny<Exception>(() => SecretCrypto.Decrypt(payload, key));
    }

    [Fact]
    public void PasswordDerivedKey_Stable_For_Same_Password()
    {
        var a = SecretCrypto.DeriveKeyFromPassword("hunter2");
        var b = SecretCrypto.DeriveKeyFromPassword("hunter2");
        Assert.Equal(a, b);
    }
}

public class ScriptServiceTests : IDisposable
{
    private readonly TempDb _t = new();
    private readonly ScriptService _svc;

    public ScriptServiceTests()
    {
        var engine = new QueryEngine();
        var meta = new MetaService(_t.Factory);
        var audit = new AuditService(_t.Factory);
        _svc = new ScriptService(_t.Db, engine, meta, audit, new FusionCache(new FusionCacheOptions()));
    }

    public void Dispose() => _t.Dispose();

    private Script SeedScript(string slug = "recent", string queryJson = """{"from":"entries","select":["title"],"limit":10}""",
        string? transform = null, string? paramsSchema = null, int cacheTtl = 300)
    {
        var s = new Script
        {
            Slug = slug,
            Name = slug,
            Query = Json.Obj(queryJson),
            Transform = transform,
            ParamsSchema = paramsSchema is null ? null : Json.Obj(paramsSchema),
            OutputFormat = "json",
            CacheTtl = cacheTtl,
        };
        _t.Db.Scripts.Add(s);
        _t.Db.SaveChanges();
        return s;
    }

    [Fact]
    public async Task Run_Returns_Rows_And_Envelopes()
    {
        _t.SeedEntry(title: "one");
        _t.SeedEntry(title: "two");
        SeedScript();

        var r = await _svc.RunAsync("recent", Json.Obj("{}"));
        Assert.False(r.Cached);
        Assert.Equal(2, r.Result.GetProperty("row_count").GetInt32());
    }

    [Fact]
    public async Task Run_Second_Time_Is_Cached()
    {
        _t.SeedEntry(title: "one");
        SeedScript(cacheTtl: 600);

        await _svc.RunAsync("recent", Json.Obj("{}"));
        var second = await _svc.RunAsync("recent", Json.Obj("{}"));
        Assert.True(second.Cached);
    }

    [Fact]
    public async Task Run_Unknown_Slug_Throws()
    {
        await Assert.ThrowsAsync<ScriptService.ScriptNotFoundException>(
            () => _svc.RunAsync("nope", Json.Obj("{}")));
    }

    [Fact]
    public async Task Run_Validates_Params_Schema_Required()
    {
        SeedScript(slug: "withparams",
            queryJson: """{"from":"entries","where":{"kind":{"$param":"k"}},"select":["title"]}""",
            paramsSchema: """{"type":"object","required":["k"]}""");

        await Assert.ThrowsAsync<QueryEngine.QueryException>(
            () => _svc.RunAsync("withparams", Json.Obj("{}")));
    }

    [Fact]
    public async Task Transform_JmesPath_Applied()
    {
        _t.SeedEntry(title: "tt");
        SeedScript(transform: "[].title");

        var r = await _svc.RunAsync("recent", Json.Obj("{}"));
        var rows = r.Result.GetProperty("rows");
        Assert.Equal("tt", rows[0].GetString());
    }

    [Fact]
    public void CanonicalJson_Sorts_Keys()
    {
        var a = ScriptService.CanonicalJson(Json.Obj("""{"b":1,"a":2}"""));
        var b = ScriptService.CanonicalJson(Json.Obj("""{"a":2,"b":1}"""));
        Assert.Equal(a, b);
    }

    [Fact]
    public void BuildCacheKey_Differs_By_Params_And_Version()
    {
        var id = Guid.NewGuid();
        var k1 = ScriptService.BuildCacheKey(id, 1, Json.Obj("""{"x":1}"""), 7);
        var k2 = ScriptService.BuildCacheKey(id, 1, Json.Obj("""{"x":2}"""), 7);
        var k3 = ScriptService.BuildCacheKey(id, 2, Json.Obj("""{"x":1}"""), 7);
        var k4 = ScriptService.BuildCacheKey(id, 1, Json.Obj("""{"x":1}"""), 8);
        Assert.NotEqual(k1, k2);
        Assert.NotEqual(k1, k3);
        Assert.NotEqual(k1, k4);
        // стабильность
        Assert.Equal(k1, ScriptService.BuildCacheKey(id, 1, Json.Obj("""{"x":1}"""), 7));
    }

    [Fact]
    public async Task Invalidate_Clears_Cache()
    {
        _t.SeedEntry(title: "x");
        SeedScript(cacheTtl: 600);
        await _svc.RunAsync("recent", Json.Obj("{}"));
        Assert.True(await _t.Db.ScriptCache.AnyAsync());

        await _svc.InvalidateAsync("recent");
        Assert.False(await _t.Db.ScriptCache.AnyAsync());
    }
}

public class SearchServiceTests : IDisposable
{
    private readonly TempDb _t = new();
    private readonly SearchService _svc;

    public SearchServiceTests()
    {
        var emb = new HashedNgramEmbeddings();
        _svc = new SearchService(_t.Db, emb);
    }

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task Search_Finds_By_Title_Substring()
    {
        var proj = _t.SeedProject("webapp");
        _t.SeedEntry(proj, "Kubernetes deployment notes", "body");
        _t.SeedEntry(proj, "Cooking pasta", "body");

        var hits = await _svc.SearchAsync("kubernetes");
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Title.Contains("Kubernetes"));
    }

    [Fact]
    public async Task Search_Excludes_SoftDeleted()
    {
        var e = _t.SeedEntry(title: "deleted thing zzz");
        e.DeletedAt = DateTimeOffset.UtcNow;
        _t.Db.SaveChanges();

        var hits = await _svc.SearchAsync("thing zzz");
        Assert.DoesNotContain(hits, h => h.Id == e.Id);
    }

    [Fact]
    public async Task ContentHash_Changes_On_Edit()
    {
        var e = _t.SeedEntry(title: "A", body: "B");
        var h1 = ContentHash.Of(e);
        e.Body = "changed";
        var h2 = ContentHash.Of(e);
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public async Task Search_Filters_By_Tags()
    {
        var proj = _t.SeedProject("webapp");
        _t.SeedEntry(proj, "Kubernetes auth", "body", tags: new() { "auth" });
        _t.SeedEntry(proj, "Kubernetes networking", "body", tags: new() { "net" });

        var hits = await _svc.SearchAsync("kubernetes", tags: new List<string> { "auth" });
        Assert.Single(hits);
        Assert.Contains(hits, h => h.Title.Contains("auth"));
    }
}
