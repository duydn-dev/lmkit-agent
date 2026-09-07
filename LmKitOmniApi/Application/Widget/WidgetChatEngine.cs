using System.Text.Json;
using System.Threading.Channels;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.AI.Filters;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.Widget;

/// <summary>
/// Seam for the widget chat engine so test hosts can substitute a canned
/// engine (the real one requires a loaded LM-Kit model).
/// </summary>
public interface IWidgetChatEngine
{
    IAsyncEnumerable<string> StreamAnswerAsync(WidgetTurnRequest request, CancellationToken ct);
}

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
/// <para>
/// Streaming shape mirrors <c>AgentOrchestrator</c>: the token event is
/// subscribed BEFORE the blocking native <c>Submit</c> starts on a dedicated
/// thread, each user-visible segment is written to an unbounded channel, and the
/// consumer drains that channel. (Historically this engine created the channel
/// but never subscribed the event, so the drained text was ALWAYS empty and the
/// endpoint emitted the canned apology after paying for a full inference;
/// <c>WidgetChatEngineTests</c> now pins the subscription.)
/// </para>
/// </summary>
public sealed class WidgetChatEngine : IWidgetChatEngine
{
    public const int MaxMessageCharacters = 2000;
    public const int MaxHistoryMessages = 10;
    public const int MaxCompletionTokens = 1024;

    /// <summary>Emitted when the guardrail blocks (or the model produced nothing).</summary>
    public const string FallbackAnswer = "Xin lỗi, tôi không thể trả lời câu hỏi này.";

    /// <summary>Fixed, server-owned system prompt. Not configurable per request.</summary>
    public const string SystemPrompt =
        "Bạn là trợ lý của một website. Trả lời ngắn gọn, hữu ích và lịch sự. " +
        "Bạn KHÔNG có công cụ nào: không đọc file, không tìm web, không thực thi code — " +
        "chỉ trả lời từ kiến thức của mình. Nếu câu hỏi yêu cầu hành động ngoài trò chuyện, " +
        "hãy đề nghị người dùng liên hệ chủ website.";

    private readonly IWidgetInferenceSessionFactory _sessions;
    private readonly OutputGuardrailFilter _outputGuardrail;
    private readonly ILogger<WidgetChatEngine> _logger;

    /// <summary>Production constructor (the one DI sees): the LM boundary is LM-Kit.</summary>
    public WidgetChatEngine(
        LmModelManager modelManager,
        OutputGuardrailFilter outputGuardrail,
        ILogger<WidgetChatEngine> logger)
        : this(new LmKitWidgetInferenceSessionFactory(modelManager), outputGuardrail, logger)
    {
    }

    /// <summary>
    /// Test constructor: substitutes ONLY the LM boundary
    /// (<see cref="IWidgetInferenceSessionFactory"/>) so the real channel/guardrail
    /// plumbing below runs unchanged without a loaded model. Deliberately
    /// <c>internal</c> — Microsoft.Extensions.DependencyInjection only considers
    /// public constructors, so this cannot make the DI registration ambiguous.
    /// </summary>
    internal WidgetChatEngine(
        IWidgetInferenceSessionFactory sessions,
        OutputGuardrailFilter outputGuardrail,
        ILogger<WidgetChatEngine> logger)
    {
        _sessions = sessions;
        _outputGuardrail = outputGuardrail;
        _logger = logger;
    }

    public async IAsyncEnumerable<string> StreamAnswerAsync(
        WidgetTurnRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var turn = Bound(request);

        // Model + single-permit chat lease + conversation. Disposal (which releases
        // the lease) happens when the caller disposes this enumerator, i.e. after
        // the inference thread has unwound below.
        await using var session = await _sessions.OpenAsync(turn, ct);

        var channel = Channel.CreateUnbounded<string>();

        // THE point of this engine: user-visible tokens are pushed into the channel
        // as the model generates them. Without this subscription nothing ever writes
        // to the channel and the guardrail sees an empty answer.
        void OnTextSegment(object? sender, WidgetTextSegmentEventArgs e)
        {
            if (e.SegmentType == TextSegmentType.UserVisible)
                channel.Writer.TryWrite(e.Text);
            // Tool arguments and internal reasoning are never surfaced by the public
            // widget: it has no tools, and reasoning must not leak to anonymous users.
        }

        session.AfterTextCompletion += OnTextSegment;

        // Submit() is a BLOCKING native call: a dedicated thread (not the ThreadPool)
        // keeps concurrent widget turns from starving the pool. The thread signals
        // llmThreadDone in a finally so the consumer can wait for native inference to
        // unwind before the session — and with it the single-permit lease — is released.
        var llmThreadDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llmThread = new Thread(() =>
        {
            try
            {
                try
                {
                    session.Submit(turn.Message, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Widget chat inference failed for tenant {TenantId}.", turn.TenantId);
                    channel.Writer.TryComplete(ex);
                    return;
                }
                channel.Writer.TryComplete();
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

        // Started OUTSIDE the try: the finally below waits on llmThreadDone, which
        // only ever completes from the thread body — so a failed Start must not
        // enter that wait.
        llmThread.Start();

        var full = new System.Text.StringBuilder();
        try
        {
            await foreach (var text in channel.Reader.ReadAllAsync(ct))
            {
                full.Append(text);
            }
        }
        finally
        {
            // Runs on every exit path (completion, client abort, enumerator disposal)
            // and BEFORE the `await using session` scope releases the inference lease.
            // TrySetResult never faults, but the catch keeps a background-thread error
            // from surfacing during lease release.
            try { await llmThreadDone.Task; } catch { /* swallow */ }
            session.AfterTextCompletion -= OnTextSegment;
        }

        // Guardrail the FULL answer before emitting a single byte — the widget has
        // no streaming holdback gate, so the whole text is withheld until safe.
        var outputContext = new AgentFilterContext
        {
            TenantId = turn.TenantId,
            UserId = null,
            UserRole = "Anonymous",
            OriginalInput = turn.Message,
            ProcessedInput = turn.Message,
            Output = full.ToString()
        };
        var outputResult = await _outputGuardrail.OnOutputAsync(outputContext, ct);
        var answer = outputResult.IsBlocked ? string.Empty : outputResult.ProcessedContent;

        if (answer.Length == 0)
        {
            yield return FallbackAnswer;
            yield break;
        }

        // Single SSE event carrying the complete (guardrailed) answer.
        yield return JsonSerializer.Serialize(new { done = true, answer });
    }

    /// <summary>
    /// Bounds the turn before it reaches the model: at most
    /// <see cref="MaxHistoryMessages"/> past turns, each capped at
    /// <see cref="MaxMessageCharacters"/>, blank role/content entries dropped.
    /// </summary>
    internal static WidgetTurnRequest Bound(WidgetTurnRequest request)
    {
        var history = new List<WidgetHistoryTurn>(Math.Min(request.History.Count, MaxHistoryMessages));
        foreach (var past in request.History)
        {
            if (history.Count >= MaxHistoryMessages) break;
            if (string.IsNullOrWhiteSpace(past.Role) || string.IsNullOrWhiteSpace(past.Content)) continue;
            history.Add(new WidgetHistoryTurn(
                past.Role,
                past.Content.Length > MaxMessageCharacters ? past.Content[..MaxMessageCharacters] : past.Content));
        }

        var message = request.Message.Length > MaxMessageCharacters
            ? request.Message[..MaxMessageCharacters]
            : request.Message;

        return new WidgetTurnRequest(request.TenantId, message, history);
    }
}

public sealed record WidgetTurnRequest(Guid TenantId, string Message, List<WidgetHistoryTurn> History);

public sealed record WidgetHistoryTurn(string Role, string Content);
