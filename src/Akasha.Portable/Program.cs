using System.Text.Json;
using Akasha.Core.Data;
using Akasha.Core.Models;
using Akasha.Core.Services;
using Akasha.Server;
using Akasha.Server.Api;
using Akasha.Server.Auth;
using Akasha.Server.Mcp;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

// ─── akasha — portable CLI / local server (ТЗ §3.2, §8) ─────────────────────
// Команды: serve | portable | sync | mcp | export | import | agent-key
// Portable-режим: SQLite + привязка строго к 127.0.0.1, никаких внешних портов.

var dataDir = Environment.GetEnvironmentVariable("AKASHA_DATA_DIR") ?? "data";
Directory.CreateDirectory(dataDir);
var sqlitePath = Path.Combine(dataDir, "akasha.db");
Environment.SetEnvironmentVariable("AKASHA_SQLITE_PATH", sqlitePath);

var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

switch (cmd)
{
    case "serve":
    case "portable":
        var portableOnly = cmd == "portable" || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AKASHA_PORTABLE"));
        if (portableOnly)
            Environment.SetEnvironmentVariable("AKASHA_PORTABLE", "1"); // Server Program.cs читает этот флаг
        await ServeAsync(portableOnly);
        break;
    case "sync":
        await SyncOnceAsync();
        break;
    case "export":
        await ExportAsync(args.Skip(1));
        break;
    case "import":
        await ImportAsync(args.Skip(1));
        break;
    case "agent-key":
        await CreateAgentKeyAsync(args.Skip(1));
        break;
    default:
        PrintHelp();
        break;
}
return;

// ═══ serve / portable ═══════════════════════════════════════════════════════
async Task ServeAsync(bool portableOnly)
{
    var builder = WebApplication.CreateBuilder(args.Skip(1).ToArray());
    builder.Services.AddAkasha(builder.Configuration, portable: true);
    builder.Services.AddRazorPages();
    builder.Services.AddServerSideBlazor();
    builder.Services.AddMudServices();
    builder.Services.AddHttpClient();

    if (portableOnly)
    {
        // ТЗ: portable слушает только localhost, без Telegram/MCP-pub
        var port = Environment.GetEnvironmentVariable("AKASHA_PORT") ?? "5173";
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
    }

    var app = builder.Build();
    // Routing ДО AuthMiddleware: scope-проверки (RequireScope) полагаются на ctx.GetEndpoint()
    app.UseRouting();
    app.UseMiddleware<AuthMiddleware>();
    app.MapAkashaApi();
    app.MapMcp();
    app.MapBlazorHub();
    app.MapFallbackToPage("/_Host");

    await Akasha.Server.ServiceCollectionExtensions.PrepareDatabaseAsync(app.Services);
    Console.WriteLine($"[akasha] portable UI: http://127.0.0.1:{Environment.GetEnvironmentVariable("AKASHA_PORT") ?? "5173"} (SQLite: {sqlitePath})");
    await app.RunAsync();
}

// ═══ sync — однократный цикл pull+push с сервером ═══════════════════════════
async Task SyncOnceAsync()
{
    var server = Environment.GetEnvironmentVariable("AKASHA_SERVER_URL")
                 ?? throw new InvalidOperationException("Set AKASHA_SERVER_URL (e.g. https://akasha.example.com)");
    var key = Environment.GetEnvironmentVariable("AKASHA_DEVICE_KEY")
              ?? throw new InvalidOperationException("Set AKASHA_DEVICE_KEY (agent key with sync scope)");
    var deviceId = Environment.GetEnvironmentVariable("AKASHA_DEVICE_ID") ?? Environment.MachineName;

    using var scope = BuildProvider().CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AkashaDbContext>();
    await db.Database.EnsureCreatedAsync();
    var svc = scope.ServiceProvider.GetRequiredService<SyncService>();

    using var http = new HttpClient { BaseAddress = new Uri(server.TrimEnd('/') + "/api") };
    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

    // 1) pull изменений с сервера с момента последней синхронизации
    var since = GetLastSync(Path.Combine(dataDir, "sync-state.json"));
    var pullUrl = $"sync/pull{(since is null ? "" : "?since=" + Uri.EscapeDataString(since.Value.ToString("O")))}";
    var pulled = await http.GetFromJsonAsync<List<SyncChangeDto>>(pullUrl) ?? new();
    int appliedPull = 0;
    if (pulled.Count > 0)
    {
        var changes = pulled.Select(c => new SyncService.SyncChange(
            c.Entity, c.Op, JsonSerializer.Deserialize<JsonElement>(c.Data.GetRawText()), c.UpdatedAt)).ToList();
        appliedPull = await svc.ApplyPulledChangesAsync(changes);
    }

    // 2) push локальных изменений
    var local = await svc.PullAsync(since ?? DateTimeOffset.UnixEpoch);
    var dtoLocal = local.Select(c => new SyncChangeDto(c.Entity, c.Op, c.Data, c.UpdatedAt)).ToList();
    int appliedPush = 0, skipped = 0;
    List<Guid> conflicts = new();
    if (dtoLocal.Count > 0)
    {
        var resp = await http.PostAsJsonAsync("sync/push", new { deviceId = deviceId, changes = dtoLocal });
        resp.EnsureSuccessStatusCode();
        var res = await resp.Content.ReadFromJsonAsync<PushResponse>();
        appliedPush = res?.Applied ?? 0; skipped = res?.SkippedOlder ?? 0; conflicts = res?.ConflictProposals ?? new();
    }

    SetLastSync(Path.Combine(dataDir, "sync-state.json"), DateTimeOffset.UtcNow);
    Console.WriteLine($"[akasha] sync done: pulled={appliedPull}, pushed={appliedPush}, skipped={skipped}, conflicts={conflicts.Count}");
}

// ═══ export / import ════════════════════════════════════════════════════════
async Task ExportAsync(IEnumerable<string> rest)
{
    var outPath = rest.FirstOrDefault() ?? Path.Combine(dataDir, $"akasha-export-{DateTimeOffset.UtcNow:yyyyMMddHHmm}.zip");
    var includeSecrets = rest.Contains("--secrets");
    using var scope = BuildProvider().CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AkashaDbContext>();
    await db.Database.EnsureCreatedAsync();
    var svc = scope.ServiceProvider.GetRequiredService<ExportService>();
    await using var fs = File.Create(outPath);
    await svc.ExportAsync(fs, includeSecrets);
    Console.WriteLine($"[akasha] exported to {outPath}{(includeSecrets ? " (incl. encrypted secrets)" : "")}");
}

async Task ImportAsync(IEnumerable<string> rest)
{
    var inPath = rest.FirstOrDefault() ?? throw new InvalidOperationException("Usage: akasha import <archive.zip>");
    using var scope = BuildProvider().CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AkashaDbContext>();
    await db.Database.EnsureCreatedAsync();
    var svc = scope.ServiceProvider.GetRequiredService<ExportService>();
    await using var fs = File.OpenRead(inPath);
    var n = await svc.ImportAsync(fs);
    Console.WriteLine($"[akasha] imported {n} objects (idempotent)");
}

// ═══ agent-key — создать ключ агента локально (для portable) ═════════════════
async Task CreateAgentKeyAsync(IEnumerable<string> rest)
{
    var name = rest.FirstOrDefault() ?? "agent";
    var scopes = rest.Skip(1).ToArray();
    using var scope = BuildProvider().CreateScope();
    var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
    var db = scope.ServiceProvider.GetRequiredService<AkashaDbContext>();
    await db.Database.EnsureCreatedAsync();
    var list = scopes.Length > 0 ? scopes.ToList() : new List<string> { Scopes.Read, Scopes.Propose };
    foreach (var s in list)
        if (!Scopes.IsValid(s)) throw new ArgumentException($"unknown scope '{s}' (valid: read, propose, scripts)");
    var (plaintext, agent) = await auth.CreateAgentAsync(name, "created via CLI", list);
    Console.WriteLine($"[akasha] agent '{agent.Name}' created.\nKEY (store securely, shown once): {plaintext}");
}

// ═══ helpers ════════════════════════════════════════════════════════════════
static ServiceProvider BuildProvider()
{
    var sc = new ServiceCollection();
    sc.AddLogging(b => b.AddSimpleConsole());
    var cfg = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .Build();
    // IConfiguration должна быть доступна DI (AuthService и др. резолвят её из контейнера)
    sc.AddSingleton<IConfiguration>(cfg);
    sc.AddAkasha(cfg, portable: true);
    return sc.BuildServiceProvider();
}

static DateTimeOffset? GetLastSync(string path)
{
    if (!File.Exists(path)) return null;
    try
    {
        using var d = JsonDocument.Parse(File.ReadAllText(path));
        if (d.RootElement.TryGetProperty("last_sync", out var v) && v.TryGetDateTimeOffset(out var dt)) return dt;
    }
    catch { }
    return null;
}

static void SetLastSync(string path, DateTimeOffset dt)
    => File.WriteAllText(path, JsonSerializer.Serialize(new { last_sync = dt }));

void PrintHelp()
{
    Console.WriteLine("""
        akasha — personal knowledge base (portable mode)

        Usage:
          akasha serve [--urls ...]      start full server (UI + API + MCP), portable SQLite storage
          akasha portable                start local-only server on http://127.0.0.1:$AKASHA_PORT (default 5173)
          akasha sync                    one-shot pull+push against AKASHA_SERVER_URL using AKASHA_DEVICE_KEY
          akasha mcp                     (alias) start server exposing only /mcp endpoint usage — see docs/mcp-tools.md
          akasha export [out.zip] [--secrets]   export store to open-format archive
          akasha import <archive.zip>           idempotent import
          akasha agent-key <name> [read propose scripts...]   create an agent API key locally

        Env: AKASHA_DATA_DIR (default ./data), AKASHA_PORT, AKASHA_SERVER_URL, AKASHA_DEVICE_KEY,
             AKASHA_OWNER_EMAIL, AKASHA_OWNER_PASSWORD
        """);
}

// ─── DTO для sync-клиента ────────────────────────────────────────────────────
record SyncChangeDto(string Entity, string Op, JsonElement Data, DateTimeOffset UpdatedAt);
record PushResponse(int Applied, int SkippedOlder, List<Guid> ConflictProposals);
