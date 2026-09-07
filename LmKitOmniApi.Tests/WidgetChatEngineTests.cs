using System.Text.Json;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Application.Widget;
using LmKitOmniApi.Infrastructure.AI.Filters;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Covers the REAL <see cref="WidgetChatEngine"/>. Only the LM boundary
/// (<see cref="IWidgetInferenceSessionFactory"/>) is faked — the channel, the
/// token subscription, the inference thread, the guardrail pass and the fallback
/// are the production code paths.
/// <para>
/// Regression pinned here: the engine used to create the channel and start the
/// inference thread WITHOUT ever subscribing the token event, so the drained text
/// was always empty, the guardrail saw "" and every widget turn answered with the
/// canned apology — after paying for a full inference and holding the
/// single-permit chat lease.
/// </para>
/// </summary>
public sealed class WidgetChatEngineTests
{
    private static readonly Guid TenantId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    [Fact]
    public async Task StreamAnswerAsync_EmitsTheTokensTheModelGenerated()
    {
        var engine = CreateEngine(new FakeSessionFactory(
            new WidgetTextSegmentEventArgs(TextSegmentType.UserVisible, "Chào "),
            new WidgetTextSegmentEventArgs(TextSegmentType.UserVisible, "bạn, "),
            new WidgetTextSegmentEventArgs(TextSegmentType.UserVisible, "cửa hàng mở cửa 8h.")));

        var answer = await ReadAnswerAsync(engine, "Mấy giờ mở cửa?");

        Assert.Equal("Chào bạn, cửa hàng mở cửa 8h.", answer);
        Assert.NotEqual(WidgetChatEngine.FallbackAnswer, answer);
    }

    [Fact]
    public async Task StreamAnswerAsync_SubscribesBeforeSubmitStarts()
    {
        // The fake raises its segments from inside Submit (exactly like the native
        // call). A subscription registered after Submit would miss every token.
        var factory = new FakeSessionFactory(
            new WidgetTextSegmentEventArgs(TextSegmentType.UserVisible, "token"));

        var answer = await ReadAnswerAsync(CreateEngine(factory), "hỏi");

        Assert.Equal("token", answer);
        Assert.True(factory.LastSession!.HadSubscriberDuringSubmit);
    }

    [Fact]
    public async Task StreamAnswerAsync_IgnoresNonUserVisibleSegments()
    {
        var engine = CreateEngine(new FakeSessionFactory(
            new WidgetTextSegmentEventArgs(TextSegmentType.InternalReasoning, "SECRET REASONING"),
            new WidgetTextSegmentEventArgs(TextSegmentType.UserVisible, "Câu trả lời."),
            new WidgetTextSegmentEventArgs(TextSegmentType.ToolInvocation, "{\"tool\":\"x\"}")));

        var answer = await ReadAnswerAsync(engine, "hỏi");

        Assert.Equal("Câu trả lời.", answer);
        Assert.DoesNotContain("SECRET REASONING", answer);
    }

    [Fact]
    public async Task StreamAnswerAsync_WhenModelProducesNothing_EmitsFallback()
    {
        var engine = CreateEngine(new FakeSessionFactory());

        var chunks = await CollectAsync(engine, "hỏi");

        Assert.Equal(WidgetChatEngine.FallbackAnswer, Assert.Single(chunks));
    }

    [Fact]
    public async Task StreamAnswerAsync_RunsTheOutputGuardrailOverTheGeneratedText()
    {
        var engine = CreateEngine(new FakeSessionFactory(
            new WidgetTextSegmentEventArgs(TextSegmentType.UserVisible, "Liên hệ private@example.com "),
            new WidgetTextSegmentEventArgs(TextSegmentType.UserVisible, "API_KEY=super-secret-value")));

        var answer = await ReadAnswerAsync(engine, "hỏi");

        Assert.DoesNotContain("private@example.com", answer);
        Assert.DoesNotContain("super-secret-value", answer);
    }

    [Fact]
    public async Task StreamAnswerAsync_ReleasesTheSessionAndUnsubscribes()
    {
        var factory = new FakeSessionFactory(
            new WidgetTextSegmentEventArgs(TextSegmentType.UserVisible, "xong"));

        await ReadAnswerAsync(CreateEngine(factory), "hỏi");

        var session = factory.LastSession!;
        // Disposal releases the single-permit chat lease — it must always happen.
        Assert.True(session.Disposed);
        // ... and the engine must not stay attached to the session's event.
        Assert.False(session.HasSubscribers);
    }

    [Fact]
    public async Task StreamAnswerAsync_PropagatesInferenceFailures()
    {
        var factory = new FakeSessionFactory { SubmitFailure = new InvalidOperationException("model exploded") };

        // The inference thread completes the channel with the failure, so the drain
        // rethrows it instead of silently answering with the fallback.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadAnswerAsync(CreateEngine(factory), "hỏi"));

        Assert.Equal("model exploded", failure.Message);
        Assert.True(factory.LastSession!.Disposed);
    }

    [Fact]
    public async Task StreamAnswerAsync_BoundsHistoryAndMessageBeforeTheModelSeesThem()
    {
        var factory = new FakeSessionFactory(
            new WidgetTextSegmentEventArgs(TextSegmentType.UserVisible, "ok"));
        var history = Enumerable.Range(0, WidgetChatEngine.MaxHistoryMessages + 5)
            .Select(index => new WidgetHistoryTurn(index % 2 == 0 ? "user" : "assistant", new string('h', 3000)))
            .Append(new WidgetHistoryTurn("user", "   "))
            .ToList();

        await ReadAnswerAsync(
            CreateEngine(factory),
            new WidgetTurnRequest(TenantId, new string('m', 5000), history));

        var seen = factory.LastRequest!;
        Assert.Equal(WidgetChatEngine.MaxHistoryMessages, seen.History.Count);
        Assert.All(seen.History, turn => Assert.Equal(WidgetChatEngine.MaxMessageCharacters, turn.Content.Length));
        Assert.Equal(WidgetChatEngine.MaxMessageCharacters, seen.Message.Length);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static WidgetChatEngine CreateEngine(IWidgetInferenceSessionFactory sessions)
        => new(
            sessions,
            new OutputGuardrailFilter(
                new PromptGuardService(NullLogger<PromptGuardService>.Instance),
                NullLogger<OutputGuardrailFilter>.Instance),
            NullLogger<WidgetChatEngine>.Instance);

    private static Task<string> ReadAnswerAsync(WidgetChatEngine engine, string message)
        => ReadAnswerAsync(engine, new WidgetTurnRequest(TenantId, message, []));

    private static async Task<string> ReadAnswerAsync(WidgetChatEngine engine, WidgetTurnRequest request)
    {
        var chunks = await CollectAsync(engine, request);
        var payload = JsonDocument.Parse(Assert.Single(chunks));
        Assert.True(payload.RootElement.GetProperty("done").GetBoolean());
        return payload.RootElement.GetProperty("answer").GetString()!;
    }

    private static Task<List<string>> CollectAsync(WidgetChatEngine engine, string message)
        => CollectAsync(engine, new WidgetTurnRequest(TenantId, message, []));

    private static async Task<List<string>> CollectAsync(WidgetChatEngine engine, WidgetTurnRequest request)
    {
        var chunks = new List<string>();
        await foreach (var chunk in engine.StreamAnswerAsync(request, CancellationToken.None))
            chunks.Add(chunk);
        return chunks;
    }

    /// <summary>Fake LM boundary: raises the scripted segments from inside Submit.</summary>
    private sealed class FakeSessionFactory(params WidgetTextSegmentEventArgs[] segments) : IWidgetInferenceSessionFactory
    {
        public Exception? SubmitFailure { get; init; }

        public WidgetTurnRequest? LastRequest { get; private set; }

        public FakeSession? LastSession { get; private set; }

        public ValueTask<IWidgetInferenceSession> OpenAsync(WidgetTurnRequest request, CancellationToken ct)
        {
            LastRequest = request;
            LastSession = new FakeSession(segments, SubmitFailure);
            return ValueTask.FromResult<IWidgetInferenceSession>(LastSession);
        }
    }

    private sealed class FakeSession(WidgetTextSegmentEventArgs[] segments, Exception? submitFailure)
        : IWidgetInferenceSession
    {
        public event EventHandler<WidgetTextSegmentEventArgs>? AfterTextCompletion;

        public bool Disposed { get; private set; }

        public bool HadSubscriberDuringSubmit { get; private set; }

        public bool HasSubscribers => AfterTextCompletion is not null;

        /// <summary>Blocking, synchronous, raises on the caller's thread — like the native Submit.</summary>
        public void Submit(string message, CancellationToken ct)
        {
            HadSubscriberDuringSubmit = AfterTextCompletion is not null;
            if (submitFailure is not null) throw submitFailure;
            foreach (var segment in segments)
            {
                ct.ThrowIfCancellationRequested();
                AfterTextCompletion?.Invoke(this, segment);
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
