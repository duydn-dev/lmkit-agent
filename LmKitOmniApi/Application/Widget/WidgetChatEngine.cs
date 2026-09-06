using System.Text.Json;
using System.Threading.Channels;
using LMKit.TextGeneration;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.AI.Filters;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.Widget;

/// <summary>
/// Minimal direct-inference chat engine for the PUBLIC widget surface.
/// Deliberately DOES NOT route through <c>AgentOrchestrator</c>: the widget
/// caller is anonymous, so the run must be direct model inference only — no
/// ReAct loop, NO tools of any kind, no memory reads/writes, no RAG. The only
/// safety surface is the output guardrail (the same
/// <see cref="OutputGuardrailFilter.OnOutputAsync"/> redaction the main chat
/// uses) applied to the FULL text before anything is emitted. Input is bounded
/// (no attachments, no tools to abuse) and the system prompt is a fixed
/// tenant-widget persona — never user-controlled.
/// Inference is serialized through the shared single-permit chat lease so the
/// widget cannot starve interactive chat of the model slot.
/// </summary>
/// <summary>
/// Seam for the widget chat engine so test hosts can substitute a canned
/// engine (the real one requires a loaded LM-Kit model).
/// </summary>
public interface IWidgetChatEngine
{
    IAsyncEnumerable<string> StreamAnswerAsync(WidgetTurnRequest request, CancellationToken ct);
}

public sealed class WidgetChatEngine(
    LmModelManager modelManager,
    OutputGuardrailFilter outputGuardrail,
    ILogger<WidgetChatEngine> logger) : IWidgetChatEngine
{
    public const int MaxMessageCharacters = 2000;
    public const int MaxHistoryMessages = 10;

    /// <summary>Fixed, server-owned system prompt. Not configurable per request.</summary>
    private const string SystemPrompt =
        "Bạn là trợ lý của một website. Trả lời ngắn gọn, hữu ích và lịch sự. " +
        "Bạn KHÔNG có công cụ nào: không đọc file, không tìm web, không thực thi code — " +
        "chỉ trả lời từ kiến thức của mình. Nếu câu hỏi yêu cầu hành động ngoài trò chuyện, " +
        "hãy đề nghị người dùng liên hệ chủ website.";

    public async IAsyncEnumerable<string> StreamAnswerAsync(
        WidgetTurnRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var inputContext = new AgentFilterContext
        {
            TenantId = request.TenantId,
            UserId = null,
            UserRole = "Anonymous",
            OriginalInput = request.Message,
            ProcessedInput = request.Message
        };

        var model = await modelManager.GetChatModelAsync(ct: ct);

        var history = new ChatHistory(model);
        var historyTaken = 0;
        foreach (var past in request.History)
        {
            if (historyTaken >= MaxHistoryMessages) break;
            if (string.IsNullOrWhiteSpace(past.Role) || string.IsNullOrWhiteSpace(past.Content)) continue;
            history.AddMessage(
                past.Role == "assistant" ? AuthorRole.Assistant : AuthorRole.User,
                past.Content.Length > MaxMessageCharacters ? past.Content[..MaxMessageCharacters] : past.Content);
            historyTaken++;
        }

        var chat = new MultiTurnConversation(model, history);
        chat.SystemPrompt = SystemPrompt;
        chat.MaximumCompletionTokens = 1024;

        var channel = Channel.CreateUnbounded<string>();
        var llmThreadDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var inferenceLease = await modelManager.AcquireChatInferenceAsync(ct);

        var llmThread = new Thread(() =>
        {
            try
            {
                try
                {
                    chat.Submit(request.Message, ct);
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Widget chat inference failed for tenant {TenantId}.", request.TenantId);
                    channel.Writer.TryComplete(ex);
                }
            }
            finally
            {
                llmThreadDone.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = $"Widget-LLM-{Guid.NewGuid():N}"
        };

        var full = new System.Text.StringBuilder();
        try
        {
            llmThread.Start();
            await foreach (var text in channel.Reader.ReadAllAsync(ct))
            {
                full.Append(text);
            }
        }
        finally
        {
            await llmThreadDone.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }

        // Guardrail the FULL answer before emitting a single byte — the widget has
        // no streaming holdback gate, so the whole text is withheld until safe.
        var outputContext = new AgentFilterContext
        {
            TenantId = request.TenantId,
            UserId = null,
            UserRole = "Anonymous",
            OriginalInput = request.Message,
            ProcessedInput = request.Message,
            Output = full.ToString()
        };
        var outputResult = await outputGuardrail.OnOutputAsync(outputContext, ct);
        var answer = outputResult.IsBlocked ? string.Empty : outputResult.ProcessedContent;

        if (answer.Length == 0)
        {
            yield return "Xin lỗi, tôi không thể trả lời câu hỏi này.";
            yield break;
        }

        // Single SSE event carrying the complete (guardrailed) answer.
        yield return JsonSerializer.Serialize(new { done = true, answer });
    }
}

public sealed record WidgetTurnRequest(Guid TenantId, string Message, List<WidgetHistoryTurn> History);

public sealed record WidgetHistoryTurn(string Role, string Content);
