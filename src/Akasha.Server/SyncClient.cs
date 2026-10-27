using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Akasha.Core;
using Akasha.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Akasha.Server;

/// <summary>
/// Клиент синхронизации portable ↔ server, используемый из UI (и CLI).
/// Конфигурация: env (AKASHA_SERVER_URL / AKASHA_DEVICE_KEY / AKASHA_DEVICE_ID)
/// либо data/sync-config.json. Курсор last_sync хранится в data/sync-state.json.
/// </summary>
public sealed class SyncClient
{
    private readonly IConfiguration _cfg;
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _configPath;
    private readonly string _statePath;

    private static readonly JsonSerializerOptions PushJsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public SyncClient(IConfiguration cfg, IHttpClientFactory httpFactory)
    {
        _cfg = cfg;
        _httpFactory = httpFactory;
        var dir = Environment.GetEnvironmentVariable("AKASHA_DATA_DIR") ?? "data";
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "sync-config.json");
        _statePath = Path.Combine(dir, "sync-state.json");
    }

    public sealed record Config(string? ServerUrl, string? DeviceKey, string? DeviceId);
    public sealed record SyncResult(int Pulled, int Pushed, int SkippedOlder, int Conflicts, DateTimeOffset At);

    public bool IsConfigured
    {
        get
        {
            var c = LoadConfig();
            return !string.IsNullOrWhiteSpace(c.ServerUrl) && !string.IsNullOrWhiteSpace(c.DeviceKey);
        }
    }

    public Config LoadConfig()
    {
        var server = _cfg["AKASHA_SERVER_URL"] ?? Environment.GetEnvironmentVariable("AKASHA_SERVER_URL");
        var key = _cfg["AKASHA_DEVICE_KEY"] ?? Environment.GetEnvironmentVariable("AKASHA_DEVICE_KEY");
        var device = _cfg["AKASHA_DEVICE_ID"] ?? Environment.GetEnvironmentVariable("AKASHA_DEVICE_ID");

        if (File.Exists(_configPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_configPath));
                var j = doc.RootElement;
                server ??= ReadStr(j, "server_url");
                key ??= ReadStr(j, "device_key");
                device ??= ReadStr(j, "device_id");
            }
            catch
            {
                // повреждённый файл — игнорируем
            }
        }

        return new Config(server, key, device ?? Environment.MachineName);
    }

    public void SaveConfig(string serverUrl, string deviceKey, string deviceId)
    {
        File.WriteAllText(_configPath, JsonSerializer.Serialize(new
        {
            server_url = serverUrl?.Trim(),
            device_key = deviceKey?.Trim(),
            device_id = deviceId?.Trim() ?? Environment.MachineName,
        }));
    }

    public DateTimeOffset? LastSync()
    {
        if (!File.Exists(_statePath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_statePath));
            if (doc.RootElement.TryGetProperty("last_sync", out var v) && v.TryGetDateTimeOffset(out var dt))
                return dt;
        }
        catch
        {
            // повреждённый курсор — начинаем с начала
        }
        return null;
    }

    /// <summary>Полный цикл: pull изменений с сервера → push локальных изменений (last-write-wins).</summary>
    public async Task<SyncResult> SyncAsync(SyncService svc, CancellationToken ct = default)
    {
        var cfg = LoadConfig();
        if (string.IsNullOrWhiteSpace(cfg.ServerUrl) || string.IsNullOrWhiteSpace(cfg.DeviceKey))
            throw new InvalidOperationException("Синхронизация не настроена: укажите URL сервера и ключ устройства.");

        var since = LastSync() ?? DateTimeOffset.UnixEpoch;
        using var http = _httpFactory.CreateClient();
        http.BaseAddress = new Uri(cfg.ServerUrl.TrimEnd('/') + "/api/v1/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.DeviceKey);

        int pulled = 0, pushed = 0, skipped = 0;
        var conflicts = new List<Guid>();

        // 1) pull
        var pullUrl = $"sync/pull?since={Uri.EscapeDataString(since.ToString("O"))}";
        var pull = await http.GetFromJsonAsync<ApiTypes.SyncPullResponse>(pullUrl, ct);
        if (pull is { Changes.Count: > 0 })
        {
            var changes = pull.Changes
                .Select(c => new SyncService.SyncChange(c.Entity, c.Op, c.Data, c.UpdatedAt))
                .ToList();
            pulled = await svc.ApplyPulledChangesAsync(changes, ct);
        }

        // 2) push
        var local = await svc.PullAsync(since, ct);
        if (local.Count > 0)
        {
            var req = new ApiTypes.SyncPushRequest(
                cfg.DeviceId ?? Environment.MachineName,
                local.Select(c => new ApiTypes.SyncChangeDto(c.Entity, c.Op, c.Data, c.UpdatedAt)).ToList());
            using var resp = await http.PostAsJsonAsync("sync/push", req, ct);
            resp.EnsureSuccessStatusCode();
            var push = await resp.Content.ReadFromJsonAsync<PushResponse>(PushJsonOpts, ct);
            pushed = push?.Applied ?? 0;
            skipped = push?.SkippedOlder ?? 0;
            conflicts = push?.ConflictProposals ?? new();
        }

        File.WriteAllText(_statePath, JsonSerializer.Serialize(new { last_sync = DateTimeOffset.UtcNow }));
        return new SyncResult(pulled, pushed, skipped, conflicts.Count, DateTimeOffset.UtcNow);
    }

    private sealed record PushResponse(int Applied, int SkippedOlder, List<Guid> ConflictProposals);

    private static string? ReadStr(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
