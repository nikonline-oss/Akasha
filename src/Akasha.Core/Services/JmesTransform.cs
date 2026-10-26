using System.Text.Json;
using DevLab.JmesPath;

namespace Akasha.Core.Services;

/// <summary>
/// JMESPath transform поверх результата скрипта. Sandbox: выражение выполняется только над
/// JSON-результатом запроса — нет доступа к файлам, сети и процессам (§12).
/// Использует JmesPath.Net (pure managed, без нативных расширений).
/// </summary>
public static class JmesTransform
{
    private static readonly JmesPath Engine = new();

    public static object Apply(List<Dictionary<string, object?>> rows, string expression)
    {
        var json = JsonSerializer.Serialize(rows, Options);
        var resultJson = Engine.Transform(json, expression);
        using var doc = JsonDocument.Parse(resultJson);
        return doc.RootElement.Clone();
    }

    /// <summary>Сериализация строк словарей в стабильный JSON для JMESPath.</summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };
}
