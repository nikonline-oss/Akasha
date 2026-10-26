using System.Text.Json;

namespace Akasha.Core.Models;

/// <summary>Секрет. НЕ синхронизируется, агентам НЕДОСТУПЕН. Значение хранится AES-GCM.</summary>
public class Secret
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ProjectId { get; set; }
    public Project? Project { get; set; }
    public string Name { get; set; } = "";
    public byte[] ValueEncrypted { get; set; } = Array.Empty<byte>();  // AES-GCM: nonce(12) + ciphertext + tag(16)
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Скрипт — сохранённый structured JSON Query с параметрами и кэшем.</summary>
public class Script
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public JsonElement Query { get; set; }             // structured query (§8.2)
    public string? Transform { get; set; }             // опциональный JMESPath
    public string OutputFormat { get; set; } = "table";// table|json|markdown|csv|graph
    public JsonElement? ParamsSchema { get; set; }     // JSON Schema параметров
    public int Version { get; set; } = 1;
    public int CacheTtl { get; set; } = 300;
    public string? CreatedBy { get; set; }             // 'owner' | agent_id
    public Guid? ApprovedProposalId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class OutputFormats
{
    public const string Table = "table";
    public const string Json = "json";
    public const string Markdown = "markdown";
    public const string Csv = "csv";
    public const string Graph = "graph";
    public static readonly string[] All = { Table, Json, Markdown, Csv, Graph };
}

/// <summary>Кэш результатов скриптов (ключ: sha256(script_id|vVer|params|dvDataVersion)).</summary>
public class ScriptCacheItem
{
    public string CacheKey { get; set; } = "";
    public Guid ScriptId { get; set; }
    public string ResultJson { get; set; } = "";
    public DateTimeOffset ComputedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public int ScriptVersion { get; set; }
    public long DataVersion { get; set; }
}

/// <summary>Мета-таблица (data_version для инвалидации кэша скриптов).</summary>
public class MetaEntry
{
    public string Key { get; set; } = "";
    public long Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Аудит всех операций владельца и агентов.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public string Actor { get; set; } = "";            // 'owner' | agent_id
    public string Action { get; set; } = "";           // create|update|delete|approve|reject|login|sync
    public string Target { get; set; } = "";
    public Guid? TargetId { get; set; }
    public string? Diff { get; set; }                  // jsonb/text
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string? Ip { get; set; }
}
