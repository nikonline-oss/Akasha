namespace Akasha.Core.Models;

/// <summary>AI-агент и его API-ключ. Plaintext ключ существует только в момент создания.</summary>
public class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";                 // "claude-desktop", "cursor-work"
    public string? Description { get; set; }
    public string KeyHash { get; set; } = "";              // argon2id(secret)
    public string KeyPrefix { get; set; } = "";            // первые 8 символов для UI
    public List<string> Scopes { get; set; } = new();      // {read, propose, scripts}
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public string? LastUsedIp { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive => RevokedAt is null;
}

public static class Scopes
{
    public const string Read = "read";
    public const string Propose = "propose";
    public const string Scripts = "scripts";

    public static readonly string[] All = { Read, Propose, Scripts };
    public static bool IsValid(string scope) => All.Contains(scope);
}
