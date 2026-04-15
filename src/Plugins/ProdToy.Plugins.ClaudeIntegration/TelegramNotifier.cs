using System.Net.Http;
using System.Text;
using System.Text.Json;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// Outbound-only Telegram bot notifier. Reads token/chat-id/per-event gates
/// from <see cref="ClaudePluginSettings"/> on every send, so configuration
/// changes take effect immediately without restarting the plugin. Fails
/// silently (into <see cref="IPluginContext.LogError"/>) on network/API errors
/// so a broken token never blocks popup or Windows-balloon delivery.
///
/// Uses a single process-wide <see cref="HttpClient"/> per .NET guidance —
/// instantiating per-call leaks sockets.
/// </summary>
sealed class TelegramNotifier
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly IPluginContext _context;

    public TelegramNotifier(IPluginContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Send a Telegram message for the given Claude hook event. Returns true
    /// if the message was accepted by Telegram (HTTP 2xx), false if disabled,
    /// gated off, misconfigured, or the API rejected the call.
    /// </summary>
    public async Task<bool> SendAsync(string title, string message, string hookEvent)
    {
        var s = _context.LoadSettings<ClaudePluginSettings>();
        if (!s.TelegramEnabled) return false;
        if (string.IsNullOrWhiteSpace(s.TelegramBotToken)) return false;
        if (string.IsNullOrWhiteSpace(s.TelegramChatId)) return false;

        bool allowed = hookEvent switch
        {
            "Stop" => s.TelegramOnStop,
            "Notification" => s.TelegramOnNotification,
            _ => false,
        };
        if (!allowed)
        {
            _context.Log($"Telegram: dropped event (hookEvent='{hookEvent}', onStop={s.TelegramOnStop}, onNotif={s.TelegramOnNotification})");
            return false;
        }

        int maxChars = s.TelegramMaxChars > 0 ? s.TelegramMaxChars : 300;
        string truncated = message.Length > maxChars
            ? message[..maxChars] + "..."
            : message;

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(s.TelegramPrefix))
            sb.Append('*').Append(EscapeMd(s.TelegramPrefix)).Append("*\n");
        sb.Append('*').Append(EscapeMd(title)).Append("*\n");
        sb.Append(EscapeMd(truncated));

        return await PostAsync(s.TelegramBotToken, s.TelegramChatId, sb.ToString());
    }

    /// <summary>
    /// Test-send used by the "Send test message" button in the settings panel.
    /// Bypasses per-event gates but still honours the master enable + credentials.
    /// </summary>
    public async Task<(bool ok, string detail)> TestSendAsync()
    {
        var s = _context.LoadSettings<ClaudePluginSettings>();
        if (string.IsNullOrWhiteSpace(s.TelegramBotToken))
            return (false, "Bot token is empty");
        if (string.IsNullOrWhiteSpace(s.TelegramChatId))
            return (false, "Chat ID is empty");

        string text = string.IsNullOrEmpty(s.TelegramPrefix)
            ? "*ProdToy test*\nTelegram integration is working."
            : $"*{EscapeMd(s.TelegramPrefix)}*\n*ProdToy test*\nTelegram integration is working.";

        try
        {
            bool ok = await PostAsync(s.TelegramBotToken, s.TelegramChatId, text);
            return ok ? (true, "Sent") : (false, "Telegram API rejected the request (see plugins.log)");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<bool> PostAsync(string token, string chatId, string text)
    {
        var payload = new { chat_id = chatId, text, parse_mode = "Markdown" };
        string json = JsonSerializer.Serialize(payload);
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        string url = $"https://api.telegram.org/bot{token}/sendMessage";

        try
        {
            using var resp = await _http.PostAsync(url, body).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                _context.LogError($"Telegram send failed ({(int)resp.StatusCode}): {err}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _context.LogError("Telegram send threw", ex);
            return false;
        }
    }

    // Minimal Telegram legacy-Markdown escape — enough to keep typical assistant
    // messages from breaking parsing. (MarkdownV2 is stricter; we stick with
    // legacy Markdown to match the existing Send-Pushover.ps1 behavior.)
    private static string EscapeMd(string s) => s
        .Replace("_", "\\_")
        .Replace("*", "\\*")
        .Replace("[", "\\[")
        .Replace("`", "\\`");
}
