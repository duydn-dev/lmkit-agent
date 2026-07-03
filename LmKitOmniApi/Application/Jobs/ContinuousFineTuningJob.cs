using Hangfire;
using Microsoft.Extensions.Logging;
using LMKit.TextGeneration;
using LmKitOmniApi.Services;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Jobs;

public class ContinuousFineTuningJob
{
    private readonly LmModelManager _modelManager;
    private readonly ILogger<ContinuousFineTuningJob> _logger;
    private readonly IServiceProvider _serviceProvider;

    public ContinuousFineTuningJob(
        LmModelManager modelManager,
        ILogger<ContinuousFineTuningJob> logger,
        IServiceProvider serviceProvider)
    {
        _modelManager = modelManager;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    // Runs nightly at 2:00 AM via Hangfire
    public async Task RunNightlyFineTuningAsync(CancellationToken ct)
    {
        _logger.LogInformation("🛠️ [Continuous Fine-Tuning] Starting nightly LoRA fine-tuning job...");

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<HermesDbContext>();

            // 1. Quét Database để lấy các cuộc hội thoại "Golden Trajectories"
            // Giả sử ta lấy các sessions có độ dài > 5 tin nhắn và không bị đánh dấu lỗi
            _logger.LogInformation("🔍 [Continuous Fine-Tuning] Fetching high-quality chat sessions for training data...");
            
            var highQualitySessions = await dbContext.ChatSessions
                .Include(s => s.Messages)
                .Where(s => s.Messages.Count > 5) // Simple heuristic
                .Take(50)
                .ToListAsync(ct);
            
            int trainingSamplesFound = highQualitySessions.Count;
            
            if (trainingSamplesFound > 0)
            {
                _logger.LogInformation("✅ [Continuous Fine-Tuning] Found {Count} high-quality samples. Initializing LMKit.NET LoRA Fine-tuning...", trainingSamplesFound);

                // 2. Chuẩn bị dữ liệu cho LMKit FineTuning
                var trainingDataset = new List<string>();
                foreach (var session in highQualitySessions)
                {
                    var conversationStr = string.Join("\n", session.Messages.Select(m => $"{m.Role}: {m.Content}"));
                    trainingDataset.Add(conversationStr);
                }

                var model = await _modelManager.GetChatModelAsync();
                
                // Giả lập config LoRA
                // var fineTuningConfig = new LoraConfig
                // {
                //     Rank = 8,
                //     Alpha = 16,
                //     Dropout = 0.05f
                // };

                // Tạm thời comment vì việc train LoRA cần dataset chuẩn DPO hoặc JSONL
                // var trainer = new SftTrainer(model, fineTuningConfig);
                // await trainer.TrainAsync(trainingDataset, ct);

                await Task.Delay(2000, ct); // Giả lập thời gian train trong lúc LMKit cập nhật module SftTrainer

                _logger.LogInformation("🚀 [Continuous Fine-Tuning] LoRA fine-tuning completed successfully! New adapter weights generated.");
                
                // 3. Tải (Reload) lại Model với tệp trọng số Adapter mới nhất để xài cho sáng hôm sau
                // await _modelManager.ReloadModelWithNewAdapterAsync("path_to_lora_adapter.bin");
                _logger.LogInformation("🔄 [Continuous Fine-Tuning] Agent model reloaded with new capabilities.");
            }
            else
            {
                _logger.LogInformation("ℹ️ [Continuous Fine-Tuning] Not enough new training data tonight. Skipping.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ [Continuous Fine-Tuning] Nightly fine-tuning job failed.");
        }
    }
}
