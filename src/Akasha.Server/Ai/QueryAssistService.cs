using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Http;

namespace Akasha.Server.Ai;

/// <summary>
/// AI-assist для конструктора скриптов: по текстовому описанию генерирует structured JSON Query.
/// Использует OpenAI-совместимый endpoint (/chat/completions) — работает с OpenAI, OpenRouter,
/// DeepSeek, Ollama и т.п. Opt-in: без AKASHA_AI_API_KEY кнопка в UI скрыта.
/// </summary>
public sealed class QueryAssistService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _endpoint;
    private readonly string? _apiKey;
    private readonly string _model;

    private const string SystemPrompt = """
        Ты — генератор structured JSON Query для Akasha (личное хранилище контекста).
        По описанию пользователя верни ТОЛЬКО один JSON-объект запроса без пояснений и без markdown.
        Формат:
        {"from":"entries|projects|entry_links","where":{...},"select":[...],"order_by":[{"field":"...","dir":"desc"}],"limit":N}
        Операторы where: $eq,$ne,$in,$contains,$contains_any,$like,$is,$gte,$relative.
        Поля entries: id,project_id,kind,title,body,tags,source_url,metadata,version,created_at,updated_at,deleted_at.
        Поля projects: id,slug,name,description,status,stack,links,created_at,updated_at,deleted_at.
        """;

    public QueryAssistService(IConfiguration cfg, IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
        _endpoint = (cfg["AKASHA_AI_ENDPOINT"] ?? "https://api.openai.com/v1").TrimEnd('/');
        _apiKey = cfg["AKASHA_AI_API_KEY"] ?? Environment.GetEnvironmentVariable("AKASHA_AI_API_KEY");
        _model = cfg["AKASHA_AI_MODEL"] ?? "gpt-4o-mini";
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<JsonElement> GenerateQueryAsync(string description, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("AI-assist не настроен: задайте AKASHA_AI_API_KEY.");

        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var body = new
        {
            model = _model,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = description },
            },
        };

        using var resp = await http.PostAsJsonAsync(_endpoint + "/chat/completions", body, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? throw new InvalidOperationException("Пустой ответ от модели.");

        return JsonDocument.Parse(ExtractJson(content)).RootElement.Clone();
    }

    private static string ExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : content;
    }
}
