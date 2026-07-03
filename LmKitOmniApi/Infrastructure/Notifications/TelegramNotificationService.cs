using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.Notifications;

public class TelegramSettings
{
    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
}

public interface ITelegramNotificationService
{
    Task SendMessageAsync(string message, CancellationToken ct = default);
}

public class TelegramNotificationService : ITelegramNotificationService
{
    private readonly HttpClient _httpClient;
    private readonly TelegramSettings _settings;
    private readonly ILogger<TelegramNotificationService> _logger;

    public TelegramNotificationService(HttpClient httpClient, IOptions<TelegramSettings> settings, ILogger<TelegramNotificationService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendMessageAsync(string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.BotToken) || string.IsNullOrWhiteSpace(_settings.ChatId) || _settings.BotToken == "YOUR_BOT_TOKEN_HERE")
        {
            _logger.LogWarning("Telegram settings are not configured. Skip sending message.");
            return;
        }

        var url = $"https://api.telegram.org/bot{_settings.BotToken}/sendMessage";
        var payload = new
        {
            chat_id = _settings.ChatId,
            text = message,
            parse_mode = "Markdown"
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await _httpClient.PostAsync(url, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("Failed to send Telegram message. Status: {Status}, Error: {Error}", response.StatusCode, error);
            }
            else
            {
                _logger.LogInformation("Successfully sent proactive notification to Telegram.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while sending Telegram message.");
        }
    }
}
