using LMKit.TextGeneration;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.Widget;

/// <summary>
/// One generated segment produced by the widget's model run. Mirrors the shape of
/// LM-Kit's <c>AfterTextCompletionEventArgs</c> (which is sealed with a non-public
/// constructor, so it cannot be raised by a test double) using the same public
/// <see cref="TextSegmentType"/> enum, so the engine's segment filtering is the
/// exact production check.
/// </summary>
public sealed class WidgetTextSegmentEventArgs(TextSegmentType segmentType, string text) : EventArgs
{
    public TextSegmentType SegmentType { get; } = segmentType;

    public string Text { get; } = text;
}

/// <summary>
/// One widget turn against the language model — the NARROW LM boundary of
/// <see cref="WidgetChatEngine"/>. Everything else (channel plumbing, token
/// subscription, guardrail, fallback) stays in the engine so it is covered by
/// tests; only this seam needs a loaded model, and tests substitute it.
/// <para>
/// Disposal releases the shared single-permit chat-inference lease, so a session
/// must always be disposed — and only AFTER <see cref="Submit"/> has unwound.
/// </para>
/// </summary>
public interface IWidgetInferenceSession : IAsyncDisposable
{
    /// <summary>
    /// Raised on the inference thread for every segment the model produces, while
    /// <see cref="Submit"/> is running. Subscribe BEFORE calling <see cref="Submit"/>
    /// or the generated text is lost.
    /// </summary>
    event EventHandler<WidgetTextSegmentEventArgs>? AfterTextCompletion;

    /// <summary>Blocking native inference call: returns when the completion is finished.</summary>
    void Submit(string message, CancellationToken ct);
}

/// <summary>Opens a per-turn <see cref="IWidgetInferenceSession"/> (model + lease + conversation).</summary>
public interface IWidgetInferenceSessionFactory
{
    ValueTask<IWidgetInferenceSession> OpenAsync(WidgetTurnRequest request, CancellationToken ct);
}

/// <summary>
/// Production LM boundary: resolves the shared chat model, acquires the
/// single-permit chat-inference lease, and builds the per-turn
/// <see cref="MultiTurnConversation"/> with the fixed widget persona.
/// </summary>
public sealed class LmKitWidgetInferenceSessionFactory(LmModelManager modelManager) : IWidgetInferenceSessionFactory
{
    public async ValueTask<IWidgetInferenceSession> OpenAsync(WidgetTurnRequest request, CancellationToken ct)
    {
        var model = await modelManager.GetChatModelAsync(ct: ct);

        var history = new ChatHistory(model);
        foreach (var past in request.History)
        {
            history.AddMessage(
                string.Equals(past.Role, "assistant", StringComparison.Ordinal) ? AuthorRole.Assistant : AuthorRole.User,
                past.Content);
        }

        // The lease is acquired last so a conversation-construction failure cannot
        // leak the single model slot; from here on the session owns its release.
        var lease = await modelManager.AcquireChatInferenceAsync(ct);
        try
        {
            return new LmKitWidgetInferenceSession(model, history, lease);
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private sealed class LmKitWidgetInferenceSession : IWidgetInferenceSession
    {
        private readonly MultiTurnConversation _chat;
        private readonly IAsyncDisposable _lease;

        public LmKitWidgetInferenceSession(LMKit.Model.LM model, ChatHistory history, IAsyncDisposable lease)
        {
            _lease = lease;
            _chat = new MultiTurnConversation(model, history)
            {
                SystemPrompt = WidgetChatEngine.SystemPrompt,
                MaximumCompletionTokens = WidgetChatEngine.MaxCompletionTokens
            };
            _chat.AfterTextCompletion += ForwardSegment;
        }

        public event EventHandler<WidgetTextSegmentEventArgs>? AfterTextCompletion;

        public void Submit(string message, CancellationToken ct) => _chat.Submit(message, ct);

        private void ForwardSegment(object? sender, LMKit.TextGeneration.Events.AfterTextCompletionEventArgs e)
            => AfterTextCompletion?.Invoke(this, new WidgetTextSegmentEventArgs(e.SegmentType, e.Text));

        public async ValueTask DisposeAsync()
        {
            _chat.AfterTextCompletion -= ForwardSegment;
            AfterTextCompletion = null;
            _chat.Dispose();
            await _lease.DisposeAsync();
        }
    }
}
