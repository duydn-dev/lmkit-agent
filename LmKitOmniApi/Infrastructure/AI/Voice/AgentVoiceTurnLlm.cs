using System.Text;
using LMKit.TextGeneration;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Services;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>
/// Real language-model step for a voice turn: turns the caller's transcript into a short,
/// speakable reply using the chat model (under the shared chat-inference lease). Uses a
/// voice-tuned system prompt (brief, natural, no markdown/lists) and a tight completion cap
/// so replies stay low-latency and TTS-friendly. This is a self-contained reply — it does
/// NOT run the full ReAct tool pipeline — which keeps a spoken turn fast; a deployment that
/// wants tools in voice can swap this seam for one that calls the orchestrator. Requires the
/// chat model to be loaded, so it runs only on a configured host; the turn loop is unit-tested
/// with a fake.
/// </summary>
public sealed class AgentVoiceTurnLlm : IVoiceTurnLlm
{
    private const string VoiceSystemPrompt =
        "Bạn là trợ lý giọng nói. Trả lời NGẮN GỌN, tự nhiên, dễ đọc thành tiếng (1–3 câu). " +
        "Không dùng markdown, không liệt kê dài, không emoji. Nếu cần làm rõ, hỏi lại một câu ngắn.";

    private const int MaxReplyTokens = 256;

    private readonly LmModelManager _models;
    private readonly ILogger<AgentVoiceTurnLlm> _logger;

    public AgentVoiceTurnLlm(LmModelManager models, ILogger<AgentVoiceTurnLlm> logger)
    {
        _models = models;
        _logger = logger;
    }

    public async Task<string> RespondAsync(string userUtterance, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userUtterance)) return string.Empty;

        var model = await _models.GetChatModelAsync(ct: ct);
        await using var lease = await _models.AcquireChatInferenceAsync(ct);

        var chat = new MultiTurnConversation(model)
        {
            SystemPrompt = VoiceSystemPrompt,
            MaximumCompletionTokens = MaxReplyTokens
        };

        var reply = new StringBuilder();
        chat.AfterTextCompletion += (_, e) =>
        {
            if (e.SegmentType == TextSegmentType.UserVisible)
                reply.Append(e.Text);
        };

        // chat.Submit is a blocking native call; run it off the request thread.
        await Task.Run(() => chat.Submit(userUtterance, ct), ct);
        return reply.ToString().Trim();
    }
}
