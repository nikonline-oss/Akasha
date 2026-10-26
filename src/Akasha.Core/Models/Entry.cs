using System.Text.Json;

namespace Akasha.Core.Models;

/// <summary>Запись контекста: заметка, решение, источник, комментарий, сниппет.</summary>
public class Entry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public string Kind { get; set; } = "note";         // note | decision | source | comment | snippet
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";             // Markdown
    public List<string> Tags { get; set; } = new();
    /// <summary>
    /// Сырое сериализованное представление Tags (разделитель \u001F) — EF-маппинг на ту же колонку,
    /// чтобы LIKE-фильтры по тегам переводились в SQL. Не использовать в бизнес-логике.
    /// </summary>
    internal string? TagsRaw { get; set; }
    public string? SourceUrl { get; set; }
    public JsonElement? Metadata { get; set; }
    /// <summary>Embedding BGE-Micro-V2 (384 dim). Провайдер БД отображает в vector (PG) / TEXT (SQLite).</summary>
    public float[]? Embedding { get; set; }
    public int Version { get; set; } = 1;
    public string? CreatedBy { get; set; }             // 'owner' | agent_id
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    /// <summary>Хеш содержимого для детекта конфликтов при синхронизации.</summary>
    public string? StateHash { get; set; }
}

public static class EntryKinds
{
    public const string Note = "note";
    public const string Decision = "decision";
    public const string Source = "source";
    public const string Comment = "comment";
    public const string Snippet = "snippet";
    public static readonly string[] All = { Note, Decision, Source, Comment, Snippet };
}

public static class LinkRelations
{
    public const string Mentions = "mentions";
    public const string Supersedes = "supersedes";
    public const string Contradicts = "contradicts";
    public const string DerivedFrom = "derived_from";
    public static readonly string[] All = { Mentions, Supersedes, Contradicts, DerivedFrom };
}

/// <summary>Связь между записями (для графа в UI).</summary>
public class EntryLink
{
    public Guid FromId { get; set; }
    public Entry? From { get; set; }
    public Guid ToId { get; set; }
    public Entry? To { get; set; }
    public string Relation { get; set; } = LinkRelations.Mentions;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
