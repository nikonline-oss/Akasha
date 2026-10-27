using Akasha.Server;
using Akasha.Server.Api;
using Akasha.Server.Mcp;
using Akasha.Server.Telegram;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// AKASHA_PORTABLE=1 включает portable-режим (SQLite + localhost-owner) — используется
// и Akasha.Portable, и интеграционными тестами (WebApplicationFactory).
var portableMode = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AKASHA_PORTABLE"));

builder.Services.AddAkasha(builder.Configuration, portable: portableMode);
if (portableMode)
    builder.Services.AddSingleton(new AuthMiddlewareOptions(true));
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddMudServices();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TelegramApproverBot>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelegramApproverBot>());

var app = builder.Build();

// Routing ДО AuthMiddleware: middleware полагается на ctx.GetEndpoint() для
// scope-проверок (RequireScope) — без выбора endpoint'а проверки не работают.
app.UseRouting();
app.UseMiddleware<AuthMiddleware>();
app.MapAkashaApi();
app.MapMcp();
app.MapGet("/login", () => Results.Content(LoginPage.Html, "text/html"));
app.MapGet("/logout", (HttpContext ctx) =>
{
    ctx.Response.Cookies.Delete(ApiEndpoints.SessionCookie);
    return Results.Redirect("/login");
});
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

await Akasha.Server.ServiceCollectionExtensions.PrepareDatabaseAsync(app.Services);

// запуск Telegram-бота, если настроен
var bot = app.Services.GetRequiredService<TelegramApproverBot>();
bot.Start(app.Services.CreateScope().ServiceProvider);

app.Run();

public partial class Program { }
