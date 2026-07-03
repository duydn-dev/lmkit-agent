using Hangfire;
using Microsoft.Extensions.Logging;
using LMKit.TextGeneration;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Services;
using LmKitOmniApi.Infrastructure.Notifications;

namespace LmKitOmniApi.Application.Jobs;

public class ProactiveMonitorJob
{
    private readonly LmModelManager _modelManager;
    private readonly ITelegramNotificationService _telegramService;
    private readonly ILogger<ProactiveMonitorJob> _logger;

    public ProactiveMonitorJob(
        LmModelManager modelManager, 
        ITelegramNotificationService telegramService,
        ILogger<ProactiveMonitorJob> logger)
    {
        _modelManager = modelManager;
        _telegramService = telegramService;
        _logger = logger;
    }

    // Runs periodically via Hangfire
    public async Task RunMonitorAsync(CancellationToken ct)
    {
        _logger.LogInformation("🚀 [Proactive Agent] Starting proactive monitor job...");

        try
        {
            // 1. Giả lập quá trình thu thập Data/Metrics từ hệ thống hoặc Database
            // Thực tế: Truy vấn Elasticsearch, Prometheus, hoặc _dbContext để lấy dữ liệu.
            var errorRate = new Random().Next(0, 15); // Random từ 0-15%
            
            if (errorRate > 10)
            {
                _logger.LogWarning("⚠️ [Proactive Agent] Detected high error rate: {Rate}%. Generating alert...", errorRate);

                // 2. Gọi Local LLM để phân tích và viết báo cáo cảnh báo
                var model = await _modelManager.GetChatModelAsync();
                var chat = new MultiTurnConversation(model);
                chat.SystemPrompt = "Bạn là AI quản trị hệ thống (Proactive Agent). Nhiệm vụ của bạn là viết một cảnh báo ngắn gọn, chuyên nghiệp (bằng tiếng Việt) gửi qua Telegram cho Quản trị viên khi phát hiện sự cố hệ thống. Hãy dùng Emoji cảnh báo.";

                var prompt = $"Hệ thống vừa báo cáo Tỷ lệ lỗi (Error Rate) tăng vọt lên {errorRate}%. Có khả năng database bị quá tải hoặc connection bị nghẽn. Hãy viết tin nhắn khẩn cấp.";
                
                var result = chat.Submit(prompt);
                var alertMessage = result.Completion.Trim();

                // 3. Đẩy tin nhắn qua Telegram
                await _telegramService.SendMessageAsync(alertMessage, ct);
                
                _logger.LogInformation("✅ [Proactive Agent] Alert sent successfully.");
            }
            else
            {
                _logger.LogInformation("✅ [Proactive Agent] System is stable. Error rate: {Rate}%. No action needed.", errorRate);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [Proactive Agent] Failed to run monitor job.");
        }
    }
}
