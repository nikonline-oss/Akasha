using System.Text.Json;

namespace Akasha.Core.Models;

/// <summary>Проект — верхняя группировка контекста.</summary>
public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Status { get; set; } = "active";     // active | paused | archived
    public List<string> Stack { get; set; } = new();
    public JsonElement? Links { get; set; }            // {repo, docs, prod}
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
}

public static class ProjectStatus
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Archived = "archived";
    public static readonly string[] All = { Active, Paused, Archived };
}
