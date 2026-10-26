using Akasha.Core.Data;
using Akasha.Core.Models;
using Akasha.Core.Services;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Akasha.Server.Telegram;

/// <summary>
/// Telegram-бот аппрувов (§7.3): уведомляет владельца о новых proposals, показывает diff
/// и принимает решение inline-кнопками Approve/Reject. Работает только с owner-chat-id
/// из конфигурации — посторонние команды бота игнорируются.
/// </summary>
public class TelegramApproverBot : BackgroundService
{
    private readonly IConfiguration _cfg;
    private IServiceProvider? _sp;
    private ITelegramBotClient? _client;

    public TelegramApproverBot(IConfiguration cfg) => _cfg = cfg;

    public void Start(IServiceProvider sp)
    {
        _sp = sp;
        var token = _cfg["AKASHA_TG_BOT_TOKEN"] ?? Environment.GetEnvironmentVariable("AKASHA_TG_BOT_TOKEN");
        if (string.IsNullOrEmpty(token)) return; // bot disabled
        _client = new TelegramBotClient(token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var chatId = _cfg["AKASHA_TG_CHAT_ID"] ?? Environment.GetEnvironmentVariable("AKASHA_TG_CHAT_ID");
        if (_client is null || string.IsNullOrEmpty(chatId)) return;

        _client.StartReceiving(Handler, onError: _ => Task.CompletedTask, stoppingToken: stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await NotifyPendingOnce(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        }
    }

    /// <summary>Уведомление о новых pending-proposals (один раз на запуск цикла).</summary>
    public async Task NotifyPendingOnce(CancellationToken ct)
    {
        if (_client is null || _sp is null) return;
        var chatId = _cfg["AKASHA_TG_CHAT_ID"];
        if (string.IsNullOrEmpty(chatId)) return;

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AkashaDbContext>();
        var diffSvc = scope.ServiceProvider.GetRequiredService<DiffService>();

        var fresh = await db.Proposals.AsNoTracking().Include(p => p.Agent)
            .Where(p => p.Status == ProposalStatus.Pending && p.NotifiedAt == null)
            .OrderByDescending(p => p.CreatedAt).Take(10).ToListAsync(ct);

        foreach (var p in fresh)
        {
            var diff = await diffSvc.BuildAsync(p, ct);
            var text = $"🔮 New proposal #{p.Id:N} by {p.Agent?.Name ?? "owner"}\n" +
                       $"{p.Op} {p.Target}{(p.TargetId is null ? "" : " [" + p.TargetId + "]")}\n\n{diff.Summary}";
            var kb = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData($"✅ Approve", $"ap:{p.Id}"),
                    InlineKeyboardButton.WithCallbackData($"❌ Reject", $"rj:{p.Id}"),
                }
            });
            await _client.SendMessageAsync(chatId, text, replyMarkup: kb, cancellationToken: ct);

            var tracked = await db.Proposals.FirstOrDefaultAsync(x => x.Id == p.Id, ct);
            if (tracked is not null) { tracked.NotifiedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); }
        }
    }

    private async Task Handler(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        var chatId = _cfg["AKASHA_TG_CHAT_ID"];
        if (update.CallbackQuery is null || string.IsNullOrEmpty(chatId) ||
            update.CallbackQuery.From.Id.ToString() != chatId) return;

        var data = update.CallbackQuery.Data ?? "";
        var parts = data.Split(':');
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out var id)) return;

        using var scope = _sp!.CreateScope();
        var proposals = scope.ServiceProvider.GetRequiredService<ProposalService>();
        try
        {
            string answer;
            if (parts[0] == "ap")
            {
                var p = await proposals.ApproveAsync(id, ct);
                answer = $"Approved ({p.Status})";
            }
            else
            {
                var p = await proposals.RejectAsync(id, "rejected via Telegram", ct);
                answer = $"Rejected ({p.Status})";
            }
            await client.AnswerCallbackQueryAsync(update.CallbackQuery.Id, answer, cancellationToken: ct);
            await client.EditMessageTextAsync(chatId, update.CallbackQuery.Message!.MessageId!,
                $"🔮 Proposal #{id:N} → {answer}", cancellationToken: ct);
        }
        catch (Exception ex)
        {
            await client.AnswerCallbackQueryAsync(update.CallbackQuery.Id, $"Error: {ex.Message}", cancellationToken: ct);
        }
    }
}
