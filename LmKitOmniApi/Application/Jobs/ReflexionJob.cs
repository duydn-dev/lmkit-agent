using Hangfire;
using Microsoft.Extensions.Logging;
using LmKitOmniApi.Services;
using LmKitOmniApi.Application.Abstractions;
using LMKit.TextGeneration.Chat;

namespace LmKitOmniApi.Application.Jobs;

/// <summary>
/// Background Job chuyên phân tích dữ liệu thất bại (Reflexion) ban đêm.
/// Rút ra Behavioral Rules cho Agent và ghi vào Vector DB.
/// </summary>
public class ReflexionJob
{
    private readonly LmModelManager _modelManager;
    private readonly IVectorStoreService _vectorStore;
    private readonly ILogger<ReflexionJob> _logger;

    public ReflexionJob(LmModelManager modelManager, IVectorStoreService vectorStore, ILogger<ReflexionJob> logger)
    {
        _modelManager = modelManager;
        _vectorStore = vectorStore;
        _logger = logger;
    }

    public async Task RunReflexionAsync(CancellationToken ct)
    {
        _logger.LogInformation("🔍 [Reflexion] Bắt đầu phân tích các phiên chat thất bại trong ngày...");
        
        // Giả lập đọc lịch sử từ Redis/Postgres có đánh dấu Negative Feedback
        var failedChatLog = "User: Bạn tìm thông tin sai rồi, công ty X thành lập năm 2020 chứ không phải 2018. Agent: Tôi xin lỗi, công ty X thành lập năm 2018.";

        var model = await _modelManager.GetChatModelAsync();
        var chat = new LMKit.TextGeneration.MultiTurnConversation(model);
        chat.SystemPrompt = "Bạn là hệ thống tự kiểm điểm (Reflexion). Hãy đọc đoạn log sau, tìm ra lỗi sai của AI và viết lại THÀNH 1 CÂU MỆNH LỆNH (Rule) ngắn gọn để AI lần sau không mắc phải. VD: 'Luôn kiểm tra kỹ năm thành lập của công ty X là 2020'.";
        
        var ruleResult = chat.Submit(failedChatLog).Completion.Trim();
        
        if (!string.IsNullOrEmpty(ruleResult))
        {
            _logger.LogInformation("💡 Đã rút ra Rule: {Rule}", ruleResult);

            // Vector hóa và lưu vào Qdrant với Payload 'BehavioralRule'
            var embeddingModel = await _modelManager.GetEmbeddingModelAsync();
            var embedder = new LMKit.Embeddings.Embedder(embeddingModel);
            var vector = embedder.GetEmbeddings(ruleResult);

            var payload = new Dictionary<string, object>
            {
                { "Type", "BehavioralRule" },
                { "Content", ruleResult }
            };

            await _vectorStore.UpsertVectorAsync("lmkit_rules", Guid.NewGuid(), vector, payload);
            _logger.LogInformation("✅ [Reflexion] Cập nhật Rule vào Vector DB thành công.");
        }
    }
}
