using Akasha.Server;
using Akasha.Server.Api;
using Akasha.Server.Mcp;
using Akasha.Server.Telegram;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAkasha(builder.Configuration, portable: false);
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddMudServices();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TelegramApproverBot>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelegramApproverBot>());

var app = builder.Build();

app.UseMiddleware<AuthMiddleware>();
app.MapAkashaApi();
app.MapMcp();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

await Akasha.Server.ServiceCollectionExtensions.PrepareDatabaseAsync(app.Services);

// запуск Telegram-бота, если настроен
var bot = app.Services.GetRequiredService<TelegramApproverBot>();
bot.Start(app.Services.CreateScope().ServiceProvider);

app.Run();

public partial class Program { }
