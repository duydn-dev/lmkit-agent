using LMKit.Agents.Tools;
using LMKit.Model;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.AI.Tools;
using Xunit;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Loads the live GGUF once for every opt-in live test, because each load costs tens of seconds
/// and multiple gigabytes. Absent the opt-in or the weights, <see cref="Model"/> is null and every
/// test in the collection skips.
/// </summary>
public sealed class LiveModelFixture : IDisposable
{
    public const string SkipReason =
        "Set LMKIT_LIVE_SEAM_TEST=1 with a local GGUF (LMKIT_LIVE_SEAM_MODEL, else the LM-Kit user "
        + "model cache) to run the live seam proofs.";

    public LM? Model { get; }

    public LiveModelFixture()
    {
        var path = LocateModel();
        if (path is not null) Model = new LM(new Uri(path));
    }

    private static string? LocateModel()
    {
        if (Environment.GetEnvironmentVariable("LMKIT_LIVE_SEAM_TEST") != "1") return null;

        var explicitPath = Environment.GetEnvironmentVariable("LMKIT_LIVE_SEAM_MODEL");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? explicitPath : null;

        var cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "models", "lm-kit");

        return Directory.Exists(cache)
            ? Directory.EnumerateFiles(cache, "*.gguf", SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    public void Dispose() => Model?.Dispose();
}

[CollectionDefinition(Name)]
public class LiveModelCollection : ICollectionFixture<LiveModelFixture>
{
    public const string Name = "lmkit-live-model";
}

/// <summary>
/// LIVE proof for the two halves of the <c>MessageCount == 0</c> defect that
/// <see cref="ChatConversationFactory"/> exists to close: the system prompt (round 3) and the
/// registered tool catalog (round 4). These are the tests that would catch a future LM-Kit release
/// changing the rule the seam is built around.
///
/// <para>
/// OPT-IN — see <see cref="LiveModelFixture"/>. Never runs in CI, which has neither the env var
/// nor the weights.
/// </para>
/// <para>
/// Measured against Llama-3.2-1B-Instruct-Q4_K_M (ChatToolCallingFormat.Default, no template
/// flags — catalog merged into the System message) and Qwen3.5-2B-Q4_K_M (format Qwen,
/// HasReasoningSupport). On BOTH, a registered tool was invoked on turn 3 in 3/3 runs through the
/// factory and 0/3 runs without the seeded catalog; without it Qwen invented plausible-looking
/// fake codes instead of calling the tool. See T1-ROUND4.md.
/// </para>
/// </summary>
[Collection(LiveModelCollection.Name)]
public class ChatConversationFactoryLiveTests(LiveModelFixture fixture)
{
    private const string Marker = "MARKER_PERSONA_LIVE";

    private const string Persona =
        Marker + ". You must answer every user message with exactly the single word: OMNI";

    /// <summary>Persona for the tool tests. Deliberately never names the tool, so finding the tool
    /// name in the prompt can only mean the CATALOG put it there.</summary>
    private const string ToolPersona =
        Marker + ". Access codes rotate constantly, so you must look up the current code with the "
        + "available tool every single time the user asks for it. Never guess a code.";

    private LM Model
    {
        get
        {
            Skip.If(fixture.Model is null, LiveModelFixture.SkipReason);
            return fixture.Model!;
        }
    }

    private static ChatHistory HistoryOf(LM model, params (AuthorRole Role, string Text)[] turns)
    {
        var history = new ChatHistory(model);
        foreach (var (role, text) in turns) history.AddMessage(role, text);
        return history;
    }

    /// <summary>Text of the prompt SCAFFOLDING only — the system/developer/tools-catalog block.
    /// Asserting on this rather than on <c>ToText()</c> keeps the assertions immune to a model
    /// that happens to echo a tool name in its own answer.</summary>
    private static string ScaffoldingText(ChatHistory history) =>
        string.Join('\n', history.Messages
            .Where(m => m.AuthorRole is AuthorRole.System or AuthorRole.Developer or AuthorRole.ToolsCatalog)
            .Select(m => m.Text ?? string.Empty));

    // ── Round 3: the system prompt ──────────────────────────────────────────

    [SkippableFact]
    public void NonEmptyHistory_SystemPromptReachesTheRenderedPrompt()
    {
        var model = Model;

        var history = HistoryOf(model, (AuthorRole.User, "hello"), (AuthorRole.Assistant, "Hi there!"));

        using var chat = ChatConversationFactory.Create(model, history, Persona);
        chat.MaximumCompletionTokens = 12;
        chat.Submit("What is the capital of France?", CancellationToken.None);

        // ToText() is the literal token-side prompt, so this is the assertion that matters:
        // reading back chat.SystemPrompt would pass even when the prompt is silently dropped.
        var rendered = chat.ChatHistory.ToText();

        Assert.Contains(Marker, rendered, StringComparison.Ordinal);
        Assert.Equal(AuthorRole.System, chat.ChatHistory.Messages[0].AuthorRole);
        Assert.Single(chat.ChatHistory.Messages, m => m.AuthorRole == AuthorRole.System);
    }

    // ── Round 4: the tool catalog ───────────────────────────────────────────

    /// <summary>
    /// The mechanical before/after, with no sampling in it at all: on a non-empty history the tool
    /// definitions are in the prompt when tools are passed to the factory and absent when they are
    /// not. The "absent" half is exactly what shipped, and is why function calling died after the
    /// first message of every session.
    /// </summary>
    [SkippableFact]
    public void NonEmptyHistory_ToolCatalogIsSeededOnlyWhenToolsArePassedToTheFactory()
    {
        var model = Model;
        var tool = new RotatingCodeTool();

        var source = HistoryOf(model,
            (AuthorRole.User, "What is the current access code?"),
            (AuthorRole.Assistant, "The current access code is ZQ7741."));

        var withTools = ChatConversationFactory.BuildHistory(model, source, ToolPersona, [tool]);
        var withoutTools = ChatConversationFactory.BuildHistory(model, source, ToolPersona);

        Assert.Contains(tool.Name, ScaffoldingText(withTools), StringComparison.Ordinal);
        Assert.DoesNotContain(tool.Name, ScaffoldingText(withoutTools), StringComparison.Ordinal);

        // The persona survives either way; the catalog is additive, never a replacement.
        Assert.Contains(Marker, ScaffoldingText(withTools), StringComparison.Ordinal);
        Assert.Contains(Marker, ScaffoldingText(withoutTools), StringComparison.Ordinal);

        // Whatever roles the model's template chose, the block leads the history and the
        // conversation turns still follow it in order.
        Assert.Equal(
            ["What is the current access code?", "The current access code is ZQ7741."],
            withTools.Messages
                .Where(m => m.AuthorRole is AuthorRole.User or AuthorRole.Assistant)
                .Select(m => m.Text));
    }

    /// <summary>
    /// Re-seeding on every turn must not stack catalogs: five consecutive rebuilds keep exactly
    /// one scaffolding block, so a long conversation does not pay for the fix in context window.
    /// </summary>
    [SkippableFact]
    public void RepeatedSeeding_KeepsExactlyOneCatalog()
    {
        var model = Model;
        var tool = new RotatingCodeTool();

        var history = HistoryOf(model, (AuthorRole.User, "turn 1"), (AuthorRole.Assistant, "answer 1"));
        var first = ChatConversationFactory.BuildHistory(model, history, ToolPersona, [tool]);
        var firstScaffoldCount = first.Messages.Count(m =>
            m.AuthorRole is AuthorRole.System or AuthorRole.Developer or AuthorRole.ToolsCatalog);

        var rebuilt = first;
        for (var turn = 0; turn < 5; turn++)
            rebuilt = ChatConversationFactory.BuildHistory(model, rebuilt, ToolPersona, [tool]);

        Assert.Equal(firstScaffoldCount, rebuilt.Messages.Count(m =>
            m.AuthorRole is AuthorRole.System or AuthorRole.Developer or AuthorRole.ToolsCatalog));
        Assert.Equal(first.MessageCount, rebuilt.MessageCount);

        var occurrences = ScaffoldingText(rebuilt).Split(tool.Name).Length - 1;
        Assert.Equal(ScaffoldingText(first).Split(tool.Name).Length - 1, occurrences);
    }

    /// <summary>
    /// The end-to-end proof, and the deliverable of round 4: the model actually CALLS a registered
    /// tool on turn 3 of a conversation whose history was rebuilt from storage.
    ///
    /// <para>
    /// The tool hands out a different code on each invocation, so turn 3 cannot be satisfied by
    /// copying turn 1's answer out of the transcript — only a fresh call produces a fresh code.
    /// <c>BeforeToolInvocation</c> is the ground truth: it fires only when LM-Kit parsed a call,
    /// matched a REGISTERED tool and invoked it. Two attempts, because a 1B model at temperature
    /// is not a deterministic oracle; the shipped behaviour scored 0/3 on both models tested.
    /// </para>
    /// </summary>
    [SkippableFact]
    public void NonEmptyHistory_ModelStillCallsARegisteredToolOnTurnThree()
    {
        var model = Model;
        Assert.True(model.HasToolCalls, "This model cannot do tool calls; pick another GGUF.");

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (RunThreeTurnConversation(model, out var turn3CalledTheTool) && turn3CalledTheTool)
                return;
        }

        Assert.Fail(
            "The model never invoked the registered tool on turn 3 in 2 attempts. Either the tool "
            + "catalog stopped reaching the rebuilt history (check LmKitToolCatalogRenderer."
            + LmKitToolCatalogRenderer.Diagnostics + ") or this model is too weak for the probe.");
    }

    private bool RunThreeTurnConversation(LM model, out bool turn3CalledTheTool)
    {
        var tool = new RotatingCodeTool();
        var transcript = new List<(AuthorRole Role, string Text)>();
        turn3CalledTheTool = false;

        string Ask(string question, out bool called)
        {
            var history = HistoryOf(model, [.. transcript]);
            using var chat = ChatConversationFactory.Create(model, history, ToolPersona, [tool]);
            chat.MaximumCompletionTokens = 200;

            var invoked = false;
            chat.BeforeToolInvocation += (_, _) => invoked = true;

            var answer = chat.Submit(question, CancellationToken.None).Completion?.Trim() ?? string.Empty;
            called = invoked;

            transcript.Add((AuthorRole.User, question));
            transcript.Add((AuthorRole.Assistant, answer));
            return answer;
        }

        // Turn 1 runs on an EMPTY history: LM-Kit renders the catalog itself. If the tool is not
        // called here the probe says nothing about turns 2..n, so bail rather than assert.
        Ask("What is the current tenant access code?", out var turn1Called);
        if (!turn1Called) return false;

        Ask("Thanks. Briefly, what is the capital of France?", out _);
        Ask("The code rotated. What is the current tenant access code now?", out turn3CalledTheTool);
        return true;
    }

    /// <summary>Hands out a different code on each call, so a turn-3 answer containing a fresh
    /// code cannot have been copied from the transcript.</summary>
    private sealed class RotatingCodeTool : ITool
    {
        private static readonly string[] Codes = ["ZQ7741", "MX9312", "TB5580", "RJ4408", "HV6127", "PN3390"];
        private int _calls;

        public string Name => "get_tenant_access_code";
        public string Description => "Returns the tenant's current rotating access code.";
        public string InputSchema => """{"type":"object","properties":{}}""";

        public Task<string> InvokeAsync(string arguments, CancellationToken cancellationToken = default)
        {
            var i = Math.Min(Interlocked.Increment(ref _calls) - 1, Codes.Length - 1);
            return Task.FromResult($$"""{"access_code":"{{Codes[i]}}"}""");
        }
    }
}
