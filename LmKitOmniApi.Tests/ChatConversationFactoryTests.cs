using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Application.Widget;
using LmKitOmniApi.Infrastructure.AI;
using Xunit;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Guards the system-prompt delivery rule in <see cref="ChatConversationFactory"/>.
///
/// <para>
/// The defect these tests exist for: assigning <c>MultiTurnConversation.SystemPrompt</c> after
/// constructing on a NON-EMPTY <see cref="ChatHistory"/> is a silent no-op. LM-Kit renders the
/// system prompt into the history only when <c>MessageCount == 0</c> at the first <c>Submit</c>,
/// and the setter neither throws nor warns — the getter even keeps returning the assigned value.
/// Every chat turn after the first therefore ran with NO persona, project instructions, custom
/// instructions, recalled memory or per-turn ReAct context.
/// </para>
/// <para>
/// These run without weights: <see cref="ChatHistory"/> accepts a null model (it just skips
/// chat-template setup), so the message plan is fully assertable in-process. The live proof that
/// the underlying LM-Kit behaviour is what it is lives in S6-ROUND3.md.
/// </para>
/// </summary>
public class ChatConversationFactoryTests
{
    private const string Prompt = "You are Omni. Follow the tenant's project instructions.";

    private static ChatHistory HistoryOf(params (AuthorRole Role, string Text)[] turns)
    {
        var history = new ChatHistory(null!);
        foreach (var (role, text) in turns) history.AddMessage(role, text);
        return history;
    }

    private static IReadOnlyList<(AuthorRole Role, string Text)> Shape(ChatHistory history)
        => history.Messages.Select(m => (m.AuthorRole, m.Text)).ToList();

    // ── PlanDelivery: the rule, in isolation ────────────────────────────────

    [Theory]
    [InlineData(0, ChatConversationFactory.SystemPromptDelivery.Property)]
    [InlineData(1, ChatConversationFactory.SystemPromptDelivery.SeededMessage)]
    [InlineData(2, ChatConversationFactory.SystemPromptDelivery.SeededMessage)]
    [InlineData(40, ChatConversationFactory.SystemPromptDelivery.SeededMessage)]
    public void PlanDelivery_SeedsWheneverTheHistoryIsNotEmpty(
        int messageCount, ChatConversationFactory.SystemPromptDelivery expected)
    {
        Assert.Equal(expected, ChatConversationFactory.PlanDelivery(messageCount, Prompt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void PlanDelivery_NoUsablePrompt_DeliversNothing(string? prompt)
    {
        Assert.Equal(ChatConversationFactory.SystemPromptDelivery.None, ChatConversationFactory.PlanDelivery(0, prompt));
        Assert.Equal(ChatConversationFactory.SystemPromptDelivery.None, ChatConversationFactory.PlanDelivery(5, prompt));
    }

    /// <summary>
    /// The empty-history path must stay on the PROPERTY, never on a seeded message: LM-Kit
    /// injects the registered tool catalog into the system block inside the very same
    /// <c>MessageCount == 0</c> branch. Seeding a message on turn 1 would suppress the catalog
    /// and silently disable tool calling — a regression this rule exists to prevent.
    /// </summary>
    [Fact]
    public void PlanDelivery_EmptyHistory_UsesThePropertySoTheToolCatalogStillRenders()
    {
        Assert.Equal(
            ChatConversationFactory.SystemPromptDelivery.Property,
            ChatConversationFactory.PlanDelivery(0, Prompt));
    }

    // ── BuildHistory: pass-through cases ────────────────────────────────────

    [Fact]
    public void BuildHistory_EmptyHistory_IsLeftEmptyAndUnwrapped()
    {
        var source = HistoryOf();
        var result = ChatConversationFactory.BuildHistory(null, source, Prompt);

        Assert.Same(source, result);
        Assert.Equal(0, result.MessageCount);
    }

    [Fact]
    public void BuildHistory_NullHistory_ProducesAnEmptyHistory()
    {
        var result = ChatConversationFactory.BuildHistory(null, null, Prompt);
        Assert.Equal(0, result.MessageCount);
    }

    [Fact]
    public void BuildHistory_NoPrompt_LeavesANonEmptyHistoryExactlyAsItWas()
    {
        var source = HistoryOf((AuthorRole.User, "hi"), (AuthorRole.Assistant, "hello"));
        var result = ChatConversationFactory.BuildHistory(null, source, systemPrompt: null);

        Assert.Same(source, result);
        Assert.Equal(
            [(AuthorRole.User, "hi"), (AuthorRole.Assistant, "hello")],
            Shape(result));
    }

    [Fact]
    public void BuildHistory_WhitespacePrompt_IsTreatedAsNoPrompt()
    {
        var source = HistoryOf((AuthorRole.User, "hi"));
        Assert.Same(source, ChatConversationFactory.BuildHistory(null, source, "   \n "));
    }

    // ── BuildHistory: the actual fix ────────────────────────────────────────

    [Fact]
    public void BuildHistory_NonEmptyHistory_SeedsTheSystemPromptFirstAndKeepsEveryTurn()
    {
        var source = HistoryOf(
            (AuthorRole.User, "turn 1"),
            (AuthorRole.Assistant, "answer 1"),
            (AuthorRole.User, "turn 2"),
            (AuthorRole.Assistant, "answer 2"));

        var result = ChatConversationFactory.BuildHistory(null, source, Prompt);

        Assert.Equal(
            [
                (AuthorRole.System, Prompt),
                (AuthorRole.User, "turn 1"),
                (AuthorRole.Assistant, "answer 1"),
                (AuthorRole.User, "turn 2"),
                (AuthorRole.Assistant, "answer 2"),
            ],
            Shape(result));
    }

    [Fact]
    public void BuildHistory_SeedIsAtIndexZero_NotAppended()
    {
        // LM-Kit will happily accept AddMessage(System) AFTER a User message, producing a
        // nonsensical (User, System) history that renders no leading system block. The seam
        // rebuilds rather than appends precisely so that cannot happen.
        var result = ChatConversationFactory.BuildHistory(null, HistoryOf((AuthorRole.User, "hi")), Prompt);

        Assert.Equal(AuthorRole.System, result.Messages[0].AuthorRole);
        Assert.Equal(Prompt, result.Messages[0].Text);
    }

    [Fact]
    public void BuildHistory_DoesNotMutateTheCallersHistory()
    {
        var source = HistoryOf((AuthorRole.User, "hi"), (AuthorRole.Assistant, "hello"));

        _ = ChatConversationFactory.BuildHistory(null, source, Prompt);

        Assert.Equal(
            [(AuthorRole.User, "hi"), (AuthorRole.Assistant, "hello")],
            Shape(source));
    }

    [Fact]
    public void BuildHistory_TrimsTheSeededPrompt()
    {
        var result = ChatConversationFactory.BuildHistory(null, HistoryOf((AuthorRole.User, "hi")), "  " + Prompt + "\n\n");
        Assert.Equal(Prompt, result.Messages[0].Text);
    }

    // ── No accumulation across turns ────────────────────────────────────────

    /// <summary>
    /// The interaction the fix has to survive: LM-Kit's constructor ADOPTS <c>Messages[0]</c> as
    /// <c>SystemPrompt</c> when it is a System message, and a submit leaves that message in the
    /// history. If a later turn seeded on top of it, System messages would pile up and every turn
    /// would carry a stale copy of the previous turn's ReAct context. Seeding replaces.
    /// </summary>
    [Fact]
    public void BuildHistory_AlreadySeededHistory_ReplacesRatherThanAccumulates()
    {
        var previouslySeeded = HistoryOf(
            (AuthorRole.System, "STALE prompt from the previous turn"),
            (AuthorRole.User, "turn 1"),
            (AuthorRole.Assistant, "answer 1"));

        var result = ChatConversationFactory.BuildHistory(null, previouslySeeded, Prompt);

        Assert.Single(result.Messages, m => m.AuthorRole == AuthorRole.System);
        Assert.Equal(
            [
                (AuthorRole.System, Prompt),
                (AuthorRole.User, "turn 1"),
                (AuthorRole.Assistant, "answer 1"),
            ],
            Shape(result));
        Assert.DoesNotContain(result.Messages, m => m.Text.Contains("STALE"));
    }

    [Fact]
    public void BuildHistory_ReSeedingRepeatedly_NeverGrowsTheSystemMessageCount()
    {
        var history = HistoryOf((AuthorRole.User, "turn 1"), (AuthorRole.Assistant, "answer 1"));

        for (var turn = 0; turn < 5; turn++)
            history = ChatConversationFactory.BuildHistory(null, history, $"prompt for turn {turn}");

        Assert.Single(history.Messages, m => m.AuthorRole == AuthorRole.System);
        Assert.Equal("prompt for turn 4", history.Messages[0].Text);
        Assert.Equal(3, history.MessageCount);
    }

    // ── Content preservation ────────────────────────────────────────────────

    [Fact]
    public void BuildHistory_PreservesMultilineAndUnicodeTurnText()
    {
        const string vietnamese = "Xin chào — đây là câu hỏi\nvới nhiều dòng.";
        var result = ChatConversationFactory.BuildHistory(null, HistoryOf((AuthorRole.User, vietnamese)), Prompt);

        Assert.Equal(vietnamese, result.Messages[1].Text);
    }

    [Fact]
    public void BuildHistory_PreservesAlternatingTurnOrderExactly()
    {
        var turns = Enumerable.Range(0, 12)
            .Select(i => (i % 2 == 0 ? AuthorRole.User : AuthorRole.Assistant, $"m{i}"))
            .ToArray();

        var result = ChatConversationFactory.BuildHistory(null, HistoryOf(turns), Prompt);

        Assert.Equal(13, result.MessageCount);
        Assert.Equal(turns.Select(t => t.Item2), result.Messages.Skip(1).Select(m => m.Text));
    }

    /// <summary>
    /// The rolling conversation summary is stored with role <c>"system"</c> but deliberately
    /// injected as <see cref="AuthorRole.User"/> by <c>StreamChatCommandHandler</c> (LM-Kit
    /// merges consecutive same-role turns, which is the intent). Seeding must not disturb it.
    /// </summary>
    [Fact]
    public void BuildHistory_LeavesTheSummaryInjectedAsUserAlone()
    {
        var source = HistoryOf(
            (AuthorRole.User, "[summary of earlier turns]"),
            (AuthorRole.Assistant, "ok"),
            (AuthorRole.User, "next question"));

        var result = ChatConversationFactory.BuildHistory(null, source, Prompt);

        Assert.Equal(
            [
                (AuthorRole.System, Prompt),
                (AuthorRole.User, "[summary of earlier turns]"),
                (AuthorRole.Assistant, "ok"),
                (AuthorRole.User, "next question"),
            ],
            Shape(result));
    }

    // ── Create() argument guard ─────────────────────────────────────────────

    [Fact]
    public void Create_NullModel_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ChatConversationFactory.Create(null!, null, Prompt));
    }

    // ── The widget path, end to end minus the model ─────────────────────────

    /// <summary>
    /// Reproduces exactly what <c>LmKitWidgetInferenceSessionFactory.OpenAsync</c> builds — a
    /// history of prior User/Assistant widget turns — and proves the real
    /// <c>WidgetChatEngine.SystemPrompt</c> now leads it. Before the fix this history went into
    /// <c>new MultiTurnConversation(model, history) { SystemPrompt = ... }</c> and the widget
    /// persona was dropped for every turn after the first.
    /// </summary>
    [Fact]
    public void WidgetReturningTurn_GetsTheWidgetPersonaSeededAtIndexZero()
    {
        var widgetHistory = HistoryOf(
            (AuthorRole.User, "what are your opening hours?"),
            (AuthorRole.Assistant, "We are open 9-5."),
            (AuthorRole.User, "and on Sunday?"));

        var result = ChatConversationFactory.BuildHistory(null, widgetHistory, WidgetChatEngine.SystemPrompt);

        Assert.Equal(AuthorRole.System, result.Messages[0].AuthorRole);
        Assert.Equal(WidgetChatEngine.SystemPrompt.Trim(), result.Messages[0].Text);
        Assert.Equal(4, result.MessageCount);
        Assert.Single(result.Messages, m => m.AuthorRole == AuthorRole.System);
    }

    /// <summary>The widget's FIRST turn carries no history and must keep the property path,
    /// so LM-Kit still renders the persona itself (and any future tool catalog with it).</summary>
    [Fact]
    public void WidgetFirstTurn_StaysOnThePropertyPath()
    {
        Assert.Equal(
            ChatConversationFactory.SystemPromptDelivery.Property,
            ChatConversationFactory.PlanDelivery(0, WidgetChatEngine.SystemPrompt));
    }
}
