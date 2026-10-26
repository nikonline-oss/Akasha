using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Akasha.Core.Data;
using Akasha.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Core.Services;

/// <summary>
/// Полный экспорт/импорт хранилища в open-формате (§1.1): tar.gz-подобный zip с JSON + Markdown.
/// Секреты включаются только при includeSecrets && known master key (зашифрованными).
/// </summary>
public class ExportService
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly AkashaDbContext _db;

    public ExportService(AkashaDbContext db) => _db = db;

    public async Task ExportAsync(Stream output, bool includeSecrets = false, CancellationToken ct = default)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        // projects.json
        var projects = await _db.Projects.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        await WriteJsonAsync(zip, "projects.json", projects.Select(p => new JsonObject
        {
            ["id"] = p.Id, ["slug"] = p.Slug, ["name"] = p.Name, ["description"] = p.Description,
            ["status"] = p.Status, ["stack"] = ToArr(p.Stack), ["links"] = p.Links?.ToString(),
            ["created_at"] = p.CreatedAt, ["updated_at"] = p.UpdatedAt, ["deleted_at"] = p.DeletedAt,
        }).ToList(), ct);

        // entries.json + entries/{id}.md
        var entries = await _db.Entries.IgnoreQueryFilters().AsNoTracking()
            .Include(e => e.Project).ToListAsync(ct);
        await WriteJsonAsync(zip, "entries.json", entries.Select(e => new JsonObject
        {
            ["id"] = e.Id, ["project_id"] = e.ProjectId, ["project_slug"] = e.Project?.Slug,
            ["kind"] = e.Kind, ["title"] = e.Title, ["tags"] = ToArr(e.Tags),
            ["source_url"] = e.SourceUrl, ["metadata"] = e.Metadata?.ToString(),
            ["version"] = e.Version, ["created_by"] = e.CreatedBy,
            ["created_at"] = e.CreatedAt, ["updated_at"] = e.UpdatedAt,
            ["deleted_at"] = e.DeletedAt, ["state_hash"] = e.StateHash,
            // body — отдельным markdown-файлом, здесь ссылка
            ["body_file"] = $"entries/{e.Id}.md",
        }).ToList(), ct);

        foreach (var e in entries)
        {
            var entry = zip.CreateEntry($"entries/{e.Id}.md");
            await using var s = entry.Open();
            await using var w = new StreamWriter(s);
            await w.WriteAsync($"# {e.Title}\n\n{e.Body}");
        }

        // links.json
        var links = await _db.EntryLinks.AsNoTracking().ToListAsync(ct);
        await WriteJsonAsync(zip, "entry_links.json", links.Select(l => new JsonObject
        {
            ["from_id"] = l.FromId, ["to_id"] = l.ToId, ["relation"] = l.Relation, ["created_at"] = l.CreatedAt,
        }).ToList(), ct);

        // scripts.json
        var scripts = await _db.Scripts.AsNoTracking().ToListAsync(ct);
        await WriteJsonAsync(zip, "scripts.json", scripts.Select(s => new JsonObject
        {
            ["id"] = s.Id, ["slug"] = s.Slug, ["name"] = s.Name, ["description"] = s.Description,
            ["query"] = JsonNode.Parse(s.Query.GetRawText()), ["transform"] = s.Transform,
            ["output_format"] = s.OutputFormat, ["params_schema"] = s.ParamsSchema?.ToString(),
            ["version"] = s.Version, ["cache_ttl"] = s.CacheTtl, ["created_by"] = s.CreatedBy,
        }).ToList(), ct);

        // proposals.json
        var proposals = await _db.Proposals.AsNoTracking().ToListAsync(ct);
        await WriteJsonAsync(zip, "proposals.json", proposals.Select(p => new JsonObject
        {
            ["id"] = p.Id, ["agent_id"] = p.AgentId, ["op"] = p.Op, ["target"] = p.Target,
            ["target_id"] = p.TargetId, ["payload"] = JsonNode.Parse(p.Payload.GetRawText()),
            ["base_version"] = p.BaseVersion, ["status"] = p.Status, ["review_comment"] = p.ReviewComment,
            ["created_at"] = p.CreatedAt,
        }).ToList(), ct);

        if (includeSecrets)
        {
            var secrets = await _db.Secrets.AsNoTracking().ToListAsync(ct);
            await WriteJsonAsync(zip, "secrets.enc.json", secrets.Select(s => new JsonObject
            {
                ["id"] = s.Id, ["project_id"] = s.ProjectId, ["name"] = s.Name, ["note"] = s.Note,
                ["value_encrypted_b64"] = Convert.ToBase64String(s.ValueEncrypted),
            }).ToList(), ct);
        }

        var manifest = new JsonObject
        {
            ["format"] = "akasha-export-v1",
            ["exported_at"] = DateTimeOffset.UtcNow,
            ["counts"] = new JsonObject
            {
                ["projects"] = projects.Count, ["entries"] = entries.Count,
                ["links"] = links.Count, ["scripts"] = scripts.Count, ["proposals"] = proposals.Count,
            },
        };
        await WriteJsonAsync(zip, "manifest.json", manifest, ct);
    }

    private static async Task WriteJsonAsync<T>(ZipArchive zip, string name, T data, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name);
        await using var s = entry.Open();
        await JsonSerializer.SerializeAsync(s, data, Indented, ct);
    }

    private static JsonArray ToArr(List<string> list) =>
        new(list.Select(t => JsonValue.Create(t)).ToArray());

    // ─── импорт ───────────────────────────────────────────────────────────────

    public async Task<int> ImportAsync(Stream input, CancellationToken ct = default)
    {
        int imported = 0;
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);

        var projects = await ReadJsonAsync(zip, "projects.json", ct);
        var projectMap = new Dictionary<Guid, Guid>(); // old id → id in this db (same ids reused)
        foreach (var pj in projects ?? new())
        {
            var id = pj["id"]!.GetValue<Guid>();
            if (await _db.Projects.AnyAsync(p => p.Id == id, ct)) continue;
            var p = new Project
            {
                Id = id,
                Slug = pj["slug"]?.GetValue<string>() ?? ProposalService.Slugify(pj["name"]?.GetValue<string>() ?? "imported"),
                Name = pj["name"]?.GetValue<string>() ?? "",
                Description = pj["description"]?.GetValue<string>(),
                Status = pj["status"]?.GetValue<string>() ?? ProjectStatus.Active,
                Stack = ReadStrList(pj["stack"]),
                Links = ParseOrNull(pj["links"]?.GetValue<string>()),
                DeletedAt = GetDate(pj, "deleted_at"),
            };
            _db.Projects.Add(p);
            projectMap[id] = p.Id;
            imported++;
        }

        var entriesMeta = await ReadJsonAsync(zip, "entries.json", ct);

        // читаем тела записей из markdown-файлов zip (ключ — body_file) до вставки
        var bodies = new Dictionary<string, string>();
        if (entriesMeta is not null)
        {
            foreach (var em in entriesMeta)
            {
                var file = em["body_file"]?.GetValue<string>();
                if (file is null) continue;
                var ze = zip.GetEntry(file);
                if (ze is null) continue;
                using var sr = new StreamReader(ze.Open());
                var raw = await sr.ReadToEndAsync(ct);
                bodies[file] = raw.StartsWith("# ") ? raw[raw.IndexOf('\n')..].TrimStart('\n') : raw;
            }
        }

        // записи добавляются до SaveChanges; проекты могут быть ещё не сохранены —
        // поэтому ProjectId резолвится по Local + БД.
        foreach (var em in entriesMeta ?? new())
        {
            var id = em["id"]!.GetValue<Guid>();
            if (await _db.Entries.AnyAsync(e => e.Id == id, ct)) continue;
            var pid = em["project_id"]?.GetValue<Guid>();
            Guid? resolved = null;
            if (pid is { } pids && _db.Projects.Local.Any(p => p.Id == pids)) resolved = pids;
            else if (pid is not null && await _db.Projects.AnyAsync(p => p.Id == pid, ct)) resolved = pid;
            else if (em["project_slug"]?.GetValue<string>() is { } slug)
            {
                resolved = _db.Projects.Local.FirstOrDefault(p => p.Slug == slug)?.Id
                           ?? await _db.Projects.Where(p => p.Slug == slug).Select(p => p.Id).FirstOrDefaultAsync(ct);
                if (resolved == Guid.Empty) resolved = null;
            }
            var e = new Entry
            {
                Id = id,
                ProjectId = resolved,
                Kind = em["kind"]?.GetValue<string>() ?? EntryKinds.Note,
                Title = em["title"]?.GetValue<string>() ?? "",
                Body = em["body_file"]?.GetValue<string>() is { } f && bodies.TryGetValue(f, out var b) ? b
                       : em["body"]?.GetValue<string>() ?? "",
                Tags = ReadStrList(em["tags"]),
                SourceUrl = em["source_url"]?.GetValue<string>(),
                Version = em["version"]?.GetValue<int>() ?? 1,
                CreatedBy = em["created_by"]?.GetValue<string>() ?? "import",
                CreatedAt = GetDate(em, "created_at") ?? DateTimeOffset.UtcNow,
                UpdatedAt = GetDate(em, "updated_at") ?? DateTimeOffset.UtcNow,
                DeletedAt = GetDate(em, "deleted_at"),
            };
            _db.Entries.Add(e);
            imported++;
        }

        await _db.SaveChangesAsync(ct); // связи ниже ссылаются на эти записи (FK)

        var links = await ReadJsonAsync(zip, "entry_links.json", ct);
        foreach (var ln in links ?? new())
        {
            var from = ln["from_id"]!.GetValue<Guid>();
            var to = ln["to_id"]!.GetValue<Guid>();
            var rel = ln["relation"]?.GetValue<string>() ?? LinkRelations.Mentions;
            if (await _db.EntryLinks.AnyAsync(l => l.FromId == from && l.ToId == to && l.Relation == rel, ct)) continue;
            if (!await _db.Entries.AnyAsync(e => e.Id == from, ct) || !await _db.Entries.AnyAsync(e => e.Id == to, ct)) continue;
            _db.EntryLinks.Add(new EntryLink { FromId = from, ToId = to, Relation = rel });
            imported++;
        }

        var scripts = await ReadJsonAsync(zip, "scripts.json", ct);
        foreach (var sc in scripts ?? new())
        {
            var slug = sc["slug"]?.GetValue<string>();
            if (slug is null || await _db.Scripts.AnyAsync(s => s.Slug == slug, ct)) continue;
            _db.Scripts.Add(new Models.Script
            {
                Slug = slug,
                Name = sc["name"]?.GetValue<string>() ?? slug,
                Description = sc["description"]?.GetValue<string>(),
                Query = JsonDocument.Parse(sc["query"]!.ToJsonString()).RootElement.Clone(),
                Transform = sc["transform"]?.GetValue<string>(),
                OutputFormat = sc["output_format"]?.GetValue<string>() ?? "table",
                ParamsSchema = ParseOrNull(sc["params_schema"]?.GetValue<string>()),
                CacheTtl = sc["cache_ttl"]?.GetValue<int>() ?? 300,
                CreatedBy = sc["created_by"]?.GetValue<string>() ?? "import",
            });
            imported++;
        }

        await _db.SaveChangesAsync(ct);
        return imported;
    }

    private static async Task<List<JsonObject>?> ReadJsonAsync(ZipArchive zip, string name, CancellationToken ct)
    {
        var ze = zip.GetEntry(name);
        if (ze is null) return null;
        await using var s = ze.Open();
        var node = await JsonNode.ParseAsync(s, cancellationToken: ct);
        return node?.AsArray()?.Select(x => (JsonObject)x!).ToList();
    }

    private static List<string> ReadStrList(JsonNode? node) =>
        node is null ? new() : node.AsArray().Where(x => x is not null).Select(x => x!.GetValue<string>()).ToList();

    private static JsonElement? ParseOrNull(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json).RootElement.Clone();

    private static DateTimeOffset? GetDate(JsonObject o, string key) =>
        o[key]?.GetValue<string>() is { } s && DateTimeOffset.TryParse(s, out var dt) ? dt : null;
}
