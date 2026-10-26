using System.Text.Json;

namespace Akasha.Core.Models;

/// <summary>Предложение изменения от агента. Агент физически не может писать напрямую.</summary>
public class Proposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? AgentId { get; set; }
    public Agent? Agent { get; set; }
    public string Op { get; set; } = "";               // create | update | delete | conflict
    public string Target { get; set; } = "";           // entries | projects | entry_links | scripts
    public Guid? TargetId { get; set; }
    public JsonElement Payload { get; set; }
    public int? BaseVersion { get; set; }              // оптимистичная блокировка
    public string Status { get; set; } = ProposalStatus.Pending;
    public string? ReviewComment { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class ProposalOp
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
    public const string Conflict = "conflict";
    public static readonly string[] All = { Create, Update, Delete, Conflict };
}

public static class ProposalTarget
{
    public const string Entries = "entries";
    public const string Projects = "projects";
    public const string EntryLinks = "entry_links";
    public const string Scripts = "scripts";
    public static readonly string[] All = { Entries, Projects, EntryLinks, Scripts };
}

public static class ProposalStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Conflict = "conflict";
    public const string Applied = "applied";
    public static readonly string[] All = { Pending, Approved, Rejected, Conflict, Applied };
}
