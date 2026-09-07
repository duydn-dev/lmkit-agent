using LMKit.Model;
using LMKit.TextGeneration;
using LMKit.TextGeneration.Chat;

namespace LmKitOmniApi.Infrastructure.AI;

/// <summary>
/// The ONE place that builds a <see cref="MultiTurnConversation"/> whose system prompt
/// actually reaches the model, whatever state the history is in.
///
/// <para>
/// <b>Why this type exists.</b> Assigning <c>chat.SystemPrompt</c> after constructing a
/// <see cref="MultiTurnConversation"/> on a NON-EMPTY <see cref="ChatHistory"/> is a silent
/// no-op. LM-Kit renders the system prompt into the history exactly once, and only when the
/// history was empty at the first <c>Submit</c>. Decompiled from LM-Kit.NET 2026.9.0, the
/// submit path opens with:
/// </para>
/// <code>
///   bool flag = history.MessageCount == 0;
///   if (flag)
///   {
///       // ...tool catalog...
///       string text = template.Render(SystemPrompt, ReasoningLevel);
///       history.Add(new ChatHistory.Message(AuthorRole.System, text));
///   }
///   // else: nothing — the system prompt is never consulted again
/// </code>
/// <para>
/// The setter neither throws nor warns, and the <c>SystemPrompt</c> GETTER still returns the
/// value you assigned — so the drop is invisible from the outside. Verified live against
/// Llama-3.2-1B-Instruct-Q4_K_M: with a two-message history, an assigned system prompt was
/// absent from <c>ChatHistory.ToText()</c> and the completion ignored an instruction it obeyed
/// verbatim on an empty history.
/// </para>
///
/// <para>
/// <b>The two delivery modes.</b> There is no single construction that is correct for both
/// cases, which is why this decision is centralized here:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <b>Empty history →</b> assign the <c>SystemPrompt</c> property and leave the history
///     empty. This is the ONLY path on which LM-Kit also injects the registered tool catalog
///     into the system block (that injection lives inside the same <c>if (flag)</c> branch).
///     Seeding a message here would suppress it and REGRESS tool calling on the first turn.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Non-empty history →</b> seed the prompt as a <see cref="AuthorRole.System"/> message
///     at index 0 and do NOT touch the property. The constructor adopts <c>Messages[0]</c> as
///     <c>SystemPrompt</c> when it is a System message, and the template renders it as the
///     system block. Assigning the property on top is ignored (verified: the seeded text
///     rendered, the assigned text did not), so this type deliberately does not assign it —
///     an assignment would only create a second, lying source of truth.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// <b>Known residual gap (NOT fixed here, by design).</b> The registered tool catalog is
/// injected on the empty-history path only. Seeding restores the persona/context but not the
/// tool definitions, because LM-Kit builds that block from an <c>internal</c> type
/// (<c>H.D.A(LM, IEnumerable&lt;ITool&gt;)</c>) with no public equivalent. That is a
/// pre-existing LM-Kit limitation with the same root cause; this factory does not make it
/// worse on any path. See S6-ROUND3.md.
/// </para>
/// </summary>
public static class ChatConversationFactory
{
    /// <summary>How the system prompt will be delivered for a given history/prompt pair.</summary>
    public enum SystemPromptDelivery
    {
        /// <summary>No usable system prompt was supplied; the conversation is built as-is.</summary>
        None,

        /// <summary>
        /// History is empty: assign <c>MultiTurnConversation.SystemPrompt</c>. LM-Kit renders it
        /// (together with the tool catalog) on the first <c>Submit</c>.
        /// </summary>
        Property,

        /// <summary>
        /// History is non-empty: seed the prompt as <see cref="AuthorRole.System"/> at index 0,
        /// because the property would be silently ignored.
        /// </summary>
        SeededMessage,
    }

    /// <summary>
    /// Pure decision function: which delivery mode applies. Split out from
    /// <see cref="Create"/> so the rule is testable without a loaded model.
    /// </summary>
    /// <param name="historyMessageCount">Message count of the history the conversation will be built on.</param>
    /// <param name="systemPrompt">The system prompt the caller wants applied, if any.</param>
    public static SystemPromptDelivery PlanDelivery(int historyMessageCount, string? systemPrompt)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt)) return SystemPromptDelivery.None;
        return historyMessageCount == 0 ? SystemPromptDelivery.Property : SystemPromptDelivery.SeededMessage;
    }

    /// <summary>
    /// Builds the history the conversation should actually be constructed on.
    ///
    /// <para>
    /// When seeding applies, the result is a NEW <see cref="ChatHistory"/> holding exactly one
    /// <see cref="AuthorRole.System"/> message — the supplied prompt — at index 0, followed by
    /// the source turns in order. Any System-role message already present in <paramref name="source"/>
    /// is DROPPED rather than kept, so re-seeding on every turn cannot accumulate a second one.
    /// That is safe in this codebase because no handler puts semantic content in a System-role
    /// <see cref="ChatHistory"/> message: <c>StreamChatCommandHandler</c> deliberately maps stored
    /// <c>"system"</c> summary rows to <see cref="AuthorRole.User"/>, and <c>WidgetInferenceSession</c>
    /// only ever adds User/Assistant. A System message reaching here can therefore only be a
    /// previously rendered or seeded prompt, which this turn's prompt supersedes.
    /// </para>
    /// <para>
    /// Message text and attachments are carried across verbatim. Ordering matters: LM-Kit will
    /// happily append a System message AFTER a User message (producing a nonsensical
    /// <c>User, System</c> history) — hence the rebuild rather than an append.
    /// </para>
    /// </summary>
    /// <param name="model">Model the history is bound to. May be <c>null</c> — <see cref="ChatHistory"/>
    /// tolerates it and simply skips chat-template setup, which is what makes this unit-testable
    /// without weights.</param>
    /// <param name="source">Existing turns, or <c>null</c> for a fresh conversation.</param>
    /// <param name="systemPrompt">The system prompt to apply, if any.</param>
    public static ChatHistory BuildHistory(LM? model, ChatHistory? source, string? systemPrompt)
    {
        var sourceMessages = source?.Messages ?? (IReadOnlyList<ChatHistory.Message>)[];
        var delivery = PlanDelivery(sourceMessages.Count, systemPrompt);

        // Nothing to seed: hand the caller's history straight back so this factory is a pure
        // pass-through on the paths it has no opinion about.
        if (delivery != SystemPromptDelivery.SeededMessage)
            return source ?? new ChatHistory(model);

        var seeded = new ChatHistory(model);
        seeded.AddMessage(new ChatHistory.Message(AuthorRole.System, systemPrompt!.Trim()));

        foreach (var message in sourceMessages)
        {
            if (message.AuthorRole == AuthorRole.System) continue;
            seeded.AddMessage(CopyOf(message));
        }

        return seeded;
    }

    /// <summary>
    /// Builds a <see cref="MultiTurnConversation"/> whose <paramref name="systemPrompt"/> is
    /// guaranteed to reach the model on this turn — the drop-in replacement for
    /// <c>new MultiTurnConversation(model, history) { SystemPrompt = ... }</c>.
    /// </summary>
    /// <param name="model">Loaded chat model.</param>
    /// <param name="history">Prior turns, or <c>null</c>/empty for a fresh conversation.</param>
    /// <param name="systemPrompt">System prompt for this turn. Null/whitespace leaves the model's default.</param>
    public static MultiTurnConversation Create(LM model, ChatHistory? history, string? systemPrompt)
    {
        ArgumentNullException.ThrowIfNull(model);

        var delivery = PlanDelivery(history?.MessageCount ?? 0, systemPrompt);
        var effectiveHistory = BuildHistory(model, history, systemPrompt);
        var chat = new MultiTurnConversation(model, effectiveHistory);

        // Only assign on the empty-history path. On the seeded path the constructor has already
        // adopted Messages[0] as SystemPrompt, and an assignment there is silently ignored by
        // LM-Kit — writing it anyway would make the property disagree with what is rendered.
        if (delivery == SystemPromptDelivery.Property)
            chat.SystemPrompt = systemPrompt;

        return chat;
    }

    /// <summary>
    /// Fresh <see cref="ChatHistory.Message"/> with the same role, text and attachment references.
    /// Messages are not reused across histories: <see cref="ChatHistory.Clone"/> is a
    /// serialize/deserialize round-trip, so LM-Kit itself never shares instances either.
    /// </summary>
    private static ChatHistory.Message CopyOf(ChatHistory.Message message)
        => message.Attachments.Count > 0
            ? new ChatHistory.Message(message.AuthorRole, message.Text, message.Attachments.ToList())
            : new ChatHistory.Message(message.AuthorRole, message.Text);
}
