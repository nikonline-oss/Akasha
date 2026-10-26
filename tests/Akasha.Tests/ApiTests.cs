using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Akasha.Core.Models;
using Akasha.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Akasha.Tests;

/// <summary>
/// Интеграционные HTTP-тесты REST API (ТЗ §5) поверх WebApplicationFactory
/// в portable-режиме (SQLite + localhost-owner).
/// </summary>
public class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTests(WebApplicationFactory<Program> factory)
    {
        // Portable-режим через ENV: Server Program.cs сам регистрирует SQLite + localhost-owner
        Environment.SetEnvironmentVariable("AKASHA_PORTABLE", "1");
        var dir = Path.Combine(Path.GetTempPath(), $"akashaportable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("AKASHA_SQLITE_PATH", Path.Combine(dir, "akasha.db"));

        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
        });
    }

    private HttpClient Client() => _factory.CreateClient();

    [Fact]
    public async Task Healthz_Is_Public_And_Ok()
    {
        var resp = await Client().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_Agent_Key_Is_Rejected_With_401()
    {
        var client = Client();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "ak_deadbeef_notarealkeyatallnotreal");
        var resp = await client.GetAsync("/api/v1/projects");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Mcp_Tools_List_Returns_All_Ten_Tools()
    {
        var client = Client();
        var body = new StringContent(
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
            "application/json");
        var resp = await client.PostAsync("/mcp", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");
        Assert.Equal(10, tools.GetArrayLength());
    }

    [Fact]
    public async Task Mcp_Query_Schema_Tool_Wraps_Response_In_Data_Markers()
    {
        var client = Client();
        var payload = new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new { name = "akasha_query_schema", arguments = new { } },
        };
        var resp = await client.PostAsync("/mcp",
            new StringContent(JsonSerializer.Serialize(payload), "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var text = doc.RootElement.GetProperty("result").GetProperty("content")[0]
            .GetProperty("text").GetString()!;
        Assert.Contains("[AKASHA_DATA_START]", text);
        Assert.Contains("[AKASHA_DATA_END]", text);
    }

    [Fact]
    public async Task Portable_Localowner_Can_Create_Project_And_Proposal_Approve_Flow()
    {
        var client = Client(); // localhost → owner без ключа (portable-режим)

        // create project via admin
        var projResp = await client.PostAsync("/api/v1/admin/projects",
            Json(new { slug = "apitest", name = "API Test Project" }));
        Assert.True(projResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"create project failed: {(int)projResp.StatusCode} {await projResp.Content.ReadAsStringAsync()}");

        // agent creates a proposal (entry) — но сначала создадим агента со скоупом propose
        var agentResp = await client.PostAsync("/api/v1/admin/agents",
            Json(new { name = "test-agent-" + Guid.NewGuid().ToString("N")[..8], description = "integration test", scopes = new[] { "read", "propose" } }));
        Assert.Equal(HttpStatusCode.OK, agentResp.StatusCode);
        using var agentDoc = JsonDocument.Parse(await agentResp.Content.ReadAsStringAsync());
        var key = agentDoc.RootElement.GetProperty("api_key").GetString()!;

        var agentClient = Client();
        agentClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var propResp = await agentClient.PostAsync("/api/v1/proposals",
            Json(new { entity = "entry", op = "create",
                       payload = new { title = "Agent note", body = "Created via API test", kind = "note", project = "apitest" } }));
        Assert.True(propResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"proposal create failed: {(int)propResp.StatusCode} {await propResp.Content.ReadAsStringAsync()}");
        using var propDoc = JsonDocument.Parse(await propResp.Content.ReadAsStringAsync());
        var proposalId = propDoc.RootElement.GetProperty("id").GetGuid();

        // owner approves
        var approveResp = await client.PostAsync($"/api/v1/admin/proposals/{proposalId}/approve",
            new StringContent("", "application/json"));
        Assert.True(approveResp.StatusCode == HttpStatusCode.OK,
            $"approve failed: {(int)approveResp.StatusCode} {await approveResp.Content.ReadAsStringAsync()}");

        // entry now visible via read endpoint
        var entriesResp = await agentClient.GetAsync("/api/v1/entries?limit=50");
        entriesResp.EnsureSuccessStatusCode();
        var entriesText = await entriesResp.Content.ReadAsStringAsync();
        Assert.Contains("Agent note", entriesText);
    }

    [Fact]
    public async Task Query_Endpoint_Rejects_Non_Whitelisted_Table()
    {
        var client = Client();
        var resp = await client.PostAsync("/api/v1/query",
            Json(new { from = "secrets", select = new { title = true } }));
        // 422 validation error (query whitelist)
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    private static StringContent Json(object o) =>
        new(JsonSerializer.Serialize(o, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        }), "application/json");
}
