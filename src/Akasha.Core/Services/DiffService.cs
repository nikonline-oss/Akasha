using System.Text.Json;
using Akasha.Core.Data;
using Akasha.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Akasha.Core.Services;

/// <summary>
/// Человекочитаемый diff для proposals (§7.3): create — «+» строки, update — side-by-side построчно,
/// delete — «-» строки, scripts — diff по полям query/transform/params_schema/output_format.
/// </summary>
public class DiffService
{
    private readonly AkashaDbContext _db;

    public DiffService(AkashaDbContext db) => _db = db;

    public record DiffLine(string Type, string Text); // "add" | "del" | "ctx"

    public record ProposalDiff(
        string Op, string Target, string? AgentName, DateTimeOffset CreatedAt,
        List<DiffLine> Title, List<DiffLine> Body, List<string>? Tags,
        Dictionary<string, List<DiffLine>>? FieldDiffs, string? ProjectSlug, string? Kind)
    {
        /// <summary>Краткое человекочитаемое резюме для уведомлений (Telegram/списки).</summary>
        public string Summary
        {
            get
            {
                var added = Title.Count(l => l.Type == "add") + Body.Count(l => l.Type == "add");
                var removed = Title.Count(l => l.Type == "del") + Body.Count(l => l.Type == "del");
                var titleLine = Title.FirstOrDefault(l => l.Type is "add" or "ctx")?.Text ?? "";
                return $"{Op} {Target}: {titleLine} (+{added}/-{removed} lines)";
            }
        }
    };

    public async Task<ProposalDiff> BuildAsync(Proposal p, CancellationToken ct = default)
    {
        var agentName = p.AgentId is { } aid
            ? await _db.Agents.Where(a => a.Id == aid).Select(a => a.Name).FirstOrDefaultAsync(ct)
            : "owner";

        var (curTitle, curBody, curTags, curProject, curKind) = await LoadCurrentAsync(p, ct);

        // Conflict-предложение из синхронизации: payload = { server: {...}, portable: {...} }.
        if (p.Op == ProposalOp.Conflict)
        {
            var srv = p.Payload.TryGetProperty("server", out var sv) ? sv : default;
            var ptb = p.Payload.TryGetProperty("portable", out var pt) ? pt : default;
            string sTitle = srv.ValueKind == JsonValueKind.Object ? GetStr(srv, "title") ?? "" : "";
            string pTitle = ptb.ValueKind == JsonValueKind.Object ? GetStr(ptb, "title") ?? "" : "";
            string sBody = srv.ValueKind == JsonValueKind.Object ? GetStr(srv, "body") ?? "" : "";
            string pBody = ptb.ValueKind == JsonValueKind.Object ? GetStr(ptb, "body") ?? "" : "";

            return new ProposalDiff(
                p.Op, p.Target, agentName, p.CreatedAt,
                LineDiff(sTitle, pTitle),
                LineDiff(sBody, pBody),
                null,
                new Dictionary<string, List<DiffLine>>
                {
                    ["server (текущая версия)"] = Prefix(("Title: " + sTitle + "\n" + sBody).Split('\n'), "ctx"),
                    ["portable (входящая версия)"] = Prefix(("Title: " + pTitle + "\n" + pBody).Split('\n'), "ctx"),
                },
                null, null);
        }

        var payload = p.Payload;
        string newTitle = GetStr(payload, "title") ?? curTitle ?? "";
        string newBody = GetStr(payload, "body") ?? curBody ?? "";
        var newTags = GetArr(payload, "tags") ?? curTags;
        string? newProject = GetStr(payload, "project") ?? curProject;
        string? newKind = GetStr(payload, "kind") ?? curKind;

        Dictionary<string, List<DiffLine>>? fieldDiffs = null;

        switch (p.Target)
        {
            case ProposalTarget.Scripts:
                fieldDiffs = await BuildScriptDiffAsync(p, ct);
                break;
            case ProposalTarget.Projects:
                fieldDiffs = new Dictionary<string, List<DiffLine>>
                {
                    ["name"] = LineDiff(curTitle, GetStr(payload, "name")),
                    ["description"] = LineDiff(null, GetStr(payload, "description")),
                    ["status"] = LineDiff(curKind, GetStr(payload, "status")),
                };
                break;
            case ProposalTarget.EntryLinks:
                fieldDiffs = new Dictionary<string, List<DiffLine>>
                {
                    ["link"] = LineDiff(
                        curProject is not null ? $"{curProject} → {curTitle}" : null,
                        $"{GetStr(payload, "from_id")} -[{GetStr(payload, "relation") ?? "mentions"}]-> {GetStr(payload, "to_id")}"),
                };
                break;
        }

        return p.Op switch
        {
            ProposalOp.Create => new ProposalDiff(p.Op, p.Target, agentName, p.CreatedAt,
                Prefix(newTitle.Split('\n'), "add"), Prefix(newBody.Split('\n'), "add"), newTags,
                fieldDiffs, newProject, newKind),
            ProposalOp.Delete => new ProposalDiff(p.Op, p.Target, agentName, p.CreatedAt,
                Prefix((curTitle ?? "").Split('\n'), "del"), Prefix((curBody ?? "").Split('\n'), "del"), curTags,
                fieldDiffs, curProject, curKind),
            _ => new ProposalDiff(p.Op, p.Target, agentName, p.CreatedAt,
                LineDiff(curTitle, GetStr(payload, "title")), LineDiff(curBody, GetStr(payload, "body")), newTags,
                fieldDiffs, newProject, newKind),
        };
    }

    private async Task<(string? title, string? body, List<string>? tags, string? project, string? kind)>
        LoadCurrentAsync(Proposal p, CancellationToken ct)
    {
        if (p.Target == ProposalTarget.Entries && p.TargetId is { } eid)
        {
            var e = await _db.Entries.AsNoTracking()
                .Include(x => x.Project)
                .FirstOrDefaultAsync(x => x.Id == eid, ct);
            if (e is not null)
                return (e.Title, e.Body, e.Tags, e.Project?.Slug, e.Kind);
        }
        if (p.Target == ProposalTarget.Projects && p.TargetId is { } pid)
        {
            var pr = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid, ct);
            if (pr is not null) return (pr.Name, pr.Description, pr.Stack, pr.Slug, pr.Status);
        }
        if (p.Target == ProposalTarget.Scripts && p.TargetId is { } sid)
        {
            var s = await _db.Scripts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sid, ct);
            if (s is not null) return (s.Name, s.Query.GetRawText(), null, s.Slug, s.OutputFormat);
        }
        return (null, null, null, null, null);
    }

    private async Task<Dictionary<string, List<DiffLine>>> BuildScriptDiffAsync(Proposal p, CancellationToken ct)
    {
        Models.Script? current = null;
        if (p.TargetId is { } sid)
            current = await _db.Scripts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct);
        else if (p.Payload.TryGetProperty("slug", out var slugEl) && slugEl.ValueKind == JsonValueKind.String)
            current = await _db.Scripts.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Slug == slugEl.GetString(), ct);

        var payload = p.Payload;
        return new Dictionary<string, List<DiffLine>>
        {
            ["query"] = LineDiff(current?.Query.GetRawText(),
                payload.TryGetProperty("query", out var q) ? q.GetRawText() : null),
            ["transform"] = LineDiff(current?.Transform, GetStr(payload, "transform")),
            ["output_format"] = LineDiff(current?.OutputFormat, GetStr(payload, "output_format")),
            ["params_schema"] = LineDiff(current?.ParamsSchema?.GetRawText(),
                payload.TryGetProperty("params_schema", out var ps) ? ps.GetRawText() : null),
        };
    }

    /// <summary>Построчный LCS-diff (для записей умеренного размера).</summary>
    public static List<DiffLine> LineDiff(string? oldText, string? newText)
    {
        var a = (oldText ?? "").Replace("\r\n", "\n").Split('\n');
        var b = (newText ?? "").Replace("\r\n", "\n").Split('\n');
        if (string.IsNullOrEmpty(oldText)) return Prefix(b.Length == 1 && b[0] == "" ? Array.Empty<string>() : b, "add");
        if (string.IsNullOrEmpty(newText)) return Prefix(a.Length == 1 && a[0] == "" ? Array.Empty<string>() : a, "del");

        int n = a.Length, m = b.Length;
        // таблица LCS (O(n*m), для заметок достаточно)
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var result = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { result.Add(new DiffLine("ctx", a[x])); x++; y++; }
            else if (dp[x + 1, y] >= dp[x, y + 1]) { result.Add(new DiffLine("del", a[x])); x++; }
            else { result.Add(new DiffLine("add", b[y])); y++; }
        }
        while (x < n) result.Add(new DiffLine("del", a[x++]));
        while (y < m) result.Add(new DiffLine("add", b[y++]));
        return result;
    }

    private static List<DiffLine> Prefix(IEnumerable<string> lines, string type) =>
        lines.Select(l => new DiffLine(type, l)).ToList();

    private static string? GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string>? GetArr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : null;
}
