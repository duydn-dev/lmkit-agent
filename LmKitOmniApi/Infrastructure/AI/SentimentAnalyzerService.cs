using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Infrastructure.AI;

public interface ISentimentAnalyzerService
{
    Task<string> AnalyzeSentimentAsync(string userInput, CancellationToken ct = default);
    string GetDynamicPersonaPrompt(string sentimentLevel);
    Task<string> AnalyzeSentimentAndGetPersonaAsync(string userInput, CancellationToken ct = default);
}

public class SentimentAnalyzerService : ISentimentAnalyzerService
{
    private readonly LmModelManager _modelManager;

    public SentimentAnalyzerService(LmModelManager modelManager)
    {
        _modelManager = modelManager;
    }

    public async Task<string> AnalyzeSentimentAsync(string userInput, CancellationToken ct = default)
    {
        var model = await _modelManager.GetChatModelAsync();
        var chat = new LMKit.TextGeneration.MultiTurnConversation(model);
        chat.SystemPrompt = "Bạn là chuyên gia tâm lý học. Chỉ trả lời bằng MỘT TỪ DUY NHẤT: 'Angry' (tức giận/gấp gáp), 'Happy' (vui vẻ/thoải mái), hoặc 'Neutral' (bình thường). Dựa vào ngữ khí câu của người dùng để quyết định.";
        
        var result = chat.Submit(userInput);
        var sentiment = result.Completion.Trim().Trim('.').Trim('\'').Trim('"');
        return sentiment;
    }

    public string GetDynamicPersonaPrompt(string sentimentLevel)
    {
        return sentimentLevel.ToLowerInvariant() switch
        {
            "angry" => "Người dùng đang vội/cáu gắt. Hãy trả lời cực kỳ ngắn gọn, trực tiếp, không giải thích dài dòng.",
            "happy" => "Người dùng đang thảnh thơi. Hãy trả lời ấm áp, thân thiện và dùng biểu tượng cảm xúc.",
            _ => "Bạn là một trợ lý AI chuyên nghiệp."
        };
    }

    public async Task<string> AnalyzeSentimentAndGetPersonaAsync(string userInput, CancellationToken ct = default)
    {
        var sentiment = await AnalyzeSentimentAsync(userInput, ct);
        return GetDynamicPersonaPrompt(sentiment);
    }
}
