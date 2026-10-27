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
    // args[0] — имя субкоманды (serve/portable), его быть в конфигурации не должно
    var hostArgs = args.Length > 0 ? args[1..] : Array.Empty<string>();
    var builder = WebApplication.CreateBuilder(hostArgs);
    builder.Services.AddAkasha(builder.Configuration, portable: true);
    builder.Services.AddRazorPages();
    builder.Services.AddServerSideBlazor();
    builder.Services.AddMudServices();
    builder.Services.AddHttpClient();

    // Порт: --port N (аргумент CLI) > AKASHA_PORT (ENV) > 5173
    var port = GetOptionValue(hostArgs, "--port") ?? Environment.GetEnvironmentVariable("AKASHA_PORT") ?? "5173";

    // Всегда localhost-привязка (ТЗ §1.1 portable; serve — dev-режим).
    // UseUrls вызываем безусловно и последним, чтобы перебить hostingDefaults["urls"] (= http://localhost:5000),
    // который CreateBuilder подставляет из аргументов/ENV и который иначе игнорирует наш порт.
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

    var app = builder.Build();
    // Routing ДО AuthMiddleware: scope-проверки (RequireScope) полагаются на ctx.GetEndpoint()
    app.UseRouting();
    app.UseMiddleware<AuthMiddleware>();
    app.MapAkashaApi();
    app.MapMcp();
    app.MapBlazorHub();
    app.MapFallbackToPage("/_Host");

    await Akasha.Server.ServiceCollectionExtensions.PrepareDatabaseAsync(app.Services);
    var url = $"http://127.0.0.1:{port}";
    Console.WriteLine($"[akasha] portable UI: {url} (SQLite: {sqlitePath})");
    // Открываем браузер сразу после того, как Kestrel начал слушать.
    app.Lifetime.ApplicationStarted.Register(() => OpenBrowser(url));
    await app.RunAsync();
}

// ═══ sync — однократный цикл pull+push с сервером (общая логика — в SyncClient) ═
async Task SyncOnceAsync()
{
    using var scope = BuildProvider().CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AkashaDbContext>();
    await db.Database.EnsureCreatedAsync();
    var svc = scope.ServiceProvider.GetRequiredService<SyncService>();
    var client = scope.ServiceProvider.GetRequiredService<SyncClient>();

    var res = await client.SyncAsync(svc);
    Console.WriteLine($"[akasha] sync done: pulled={res.Pulled}, pushed={res.Pushed}, skipped={res.SkippedOlder}, conflicts={res.Conflicts}");
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
static string? GetOptionValue(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
    return null;
}

/// <summary>Открывает URL в системном браузере (best-effort: в headless/CI — тихо пропускаем).</summary>
static void OpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows())
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"") { CreateNoWindow = true });
        else if (OperatingSystem.IsMacOS())
            System.Diagnostics.Process.Start("open", url);
        else
            System.Diagnostics.Process.Start("xdg-open", url);
    }
    catch
    {
        // Браузер открыть не удалось — не критично.
    }
}

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
    sc.AddHttpClient();
    return sc.BuildServiceProvider();
}

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
