using LMKit.Agents.Tools;
using LMKit.Model;
using LMKit.TextGeneration;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Infrastructure.AI.Tools;

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
/// <b>The tool catalog rides the same branch — and is now seeded too.</b> LM-Kit injects the
/// registered tool catalog inside that identical <c>if (MessageCount == 0)</c> block, so it too
/// was lost from turn 2 onward: function calling worked on the first message of a session and
/// silently stopped for the rest of it. Passing <c>tools</c> to <see cref="Create"/> fixes that:
/// on a non-empty history the head of the rebuilt history is LM-Kit's OWN rendered
/// system+catalog block, obtained through <see cref="LmKitToolCatalogRenderer"/>. Tool parsing
/// and invocation were never gated on the branch — only on <c>Tools.Count &gt; 0</c> — so
/// restoring the catalog text is the whole fix. Verified live: with a rotating-code tool the
/// model called it on turn 3 in 3/3 runs with seeding and 0/3 without. See T1-ROUND4.md.
/// </para>
/// <para>
/// If the renderer cannot bind (a future LM-Kit reshapes the internal it targets), seeding
/// degrades to the plain system message — i.e. exactly the behaviour that shipped before this
/// change, never something worse.
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

    /// <summary>How the registered tool catalog will reach the model for a given history.</summary>
    public enum ToolCatalogDelivery
    {
        /// <summary>No tools registered; nothing to deliver.</summary>
        NotNeeded,

        /// <summary>
        /// History is empty: LM-Kit renders the catalog itself at the first <c>Submit</c>, as long
        /// as the tools are registered before it. Seeding here would SUPPRESS that render.
        /// </summary>
        LmKitRenders,

        /// <summary>
        /// History is non-empty: LM-Kit will not render the catalog, so this factory seeds
        /// LM-Kit's own rendered block at the head of the history instead.
        /// </summary>
        SeededMessages,

        /// <summary>
        /// History is non-empty and the catalog cannot be rendered (no model, or the LM-Kit
        /// renderer did not bind). The turn runs without tool definitions — the pre-fix
        /// behaviour — rather than with a hand-rolled block the model might mis-parse.
        /// </summary>
        Unavailable,
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
    /// Pure decision function for the tool catalog — the same <c>MessageCount == 0</c> rule the
    /// system prompt obeys, because LM-Kit renders both inside the one branch.
    /// </summary>
    /// <param name="historyMessageCount">Message count of the history the conversation will be built on.</param>
    /// <param name="toolCount">Number of tools the caller will register.</param>
    /// <param name="rendererAvailable">
    /// Whether LM-Kit's catalog renderer is reachable for this call — i.e. a model was supplied
    /// AND <see cref="LmKitToolCatalogRenderer.IsAvailable"/>.
    /// </param>
    public static ToolCatalogDelivery PlanToolCatalog(int historyMessageCount, int toolCount, bool rendererAvailable)
    {
        if (toolCount <= 0) return ToolCatalogDelivery.NotNeeded;
        if (historyMessageCount == 0) return ToolCatalogDelivery.LmKitRenders;
        return rendererAvailable ? ToolCatalogDelivery.SeededMessages : ToolCatalogDelivery.Unavailable;
    }

    /// <summary>
    /// Roles that are prompt scaffolding rather than conversation. They are dropped when a
    /// history is rebuilt, so re-seeding on every turn can never stack a second persona or a
    /// second (possibly stale) tool catalog on top of the first.
    /// </summary>
    private static bool IsPromptScaffolding(AuthorRole role) =>
        role is AuthorRole.System or AuthorRole.Developer or AuthorRole.ToolsCatalog;

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
    /// <para>
    /// When <paramref name="tools"/> are supplied and the history is non-empty, the head is not a
    /// bare System message but LM-Kit's OWN rendered system+catalog block
    /// (<see cref="LmKitToolCatalogRenderer.Render"/>) — which is what puts the tool definitions
    /// back in front of the model on turns 2..n. That block may be one merged System message or a
    /// System plus a Developer/ToolsCatalog message, depending on the model's chat template;
    /// LM-Kit decides, this method just prepends what it produced. Previously seeded
    /// Developer/ToolsCatalog messages are dropped from <paramref name="source"/> alongside System
    /// ones, so re-seeding every turn cannot stack catalogs. If the renderer is unavailable, the
    /// head falls back to the bare System message — the behaviour that shipped before.
    /// </para>
    /// </summary>
    /// <param name="model">Model the history is bound to. May be <c>null</c> — <see cref="ChatHistory"/>
    /// tolerates it and simply skips chat-template setup, which is what makes this unit-testable
    /// without weights. A null model also disables catalog seeding, since rendering the catalog
    /// needs the model's chat template.</param>
    /// <param name="source">Existing turns, or <c>null</c> for a fresh conversation.</param>
    /// <param name="systemPrompt">The system prompt to apply, if any.</param>
    /// <param name="tools">Tools that will be registered on the conversation, if any.</param>
    public static ChatHistory BuildHistory(
        LM? model, ChatHistory? source, string? systemPrompt, IReadOnlyList<ITool>? tools = null)
    {
        var sourceMessages = source?.Messages ?? (IReadOnlyList<ChatHistory.Message>)[];
        var delivery = PlanDelivery(sourceMessages.Count, systemPrompt);
        var catalogDelivery = PlanToolCatalog(
            sourceMessages.Count,
            tools?.Count ?? 0,
            model is not null && LmKitToolCatalogRenderer.IsAvailable);

        // Nothing to seed: hand the caller's history straight back so this factory is a pure
        // pass-through on the paths it has no opinion about. Notably the EMPTY-history path,
        // where LM-Kit renders the prompt AND the catalog itself — seeding there would suppress
        // the catalog and regress tool calling on the first turn.
        if (delivery != SystemPromptDelivery.SeededMessage
            && catalogDelivery != ToolCatalogDelivery.SeededMessages)
            return source ?? new ChatHistory(model);

        var seeded = new ChatHistory(model);

        var head = catalogDelivery == ToolCatalogDelivery.SeededMessages
            ? LmKitToolCatalogRenderer.Render(model!, systemPrompt?.Trim(), tools)
            : [];

        if (head.Count > 0)
        {
            // LM-Kit's own block already carries the system prompt in whatever shape this
            // model's template wants, so it replaces the bare System message rather than
            // joining it.
            foreach (var message in head) seeded.AddMessage(message);
        }
        else if (delivery == SystemPromptDelivery.SeededMessage)
        {
            seeded.AddMessage(new ChatHistory.Message(AuthorRole.System, systemPrompt!.Trim()));
        }

        foreach (var message in sourceMessages)
        {
            if (IsPromptScaffolding(message.AuthorRole)) continue;
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
    /// <param name="tools">
    /// Tools to make callable this turn. Pass them HERE rather than registering after the call:
    /// on a non-empty history the catalog has to be rendered into the history being built, and a
    /// registration that lands after construction is advertised to the model on the first turn
    /// only. Registration itself is still performed here, so callers need not repeat it.
    /// </param>
    public static MultiTurnConversation Create(
        LM model, ChatHistory? history, string? systemPrompt, IReadOnlyList<ITool>? tools = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        var delivery = PlanDelivery(history?.MessageCount ?? 0, systemPrompt);
        var effectiveHistory = BuildHistory(model, history, systemPrompt, tools);
        var chat = new MultiTurnConversation(model, effectiveHistory);

        // Only assign on the empty-history path. On the seeded path the constructor has already
        // adopted Messages[0] as SystemPrompt, and an assignment there is silently ignored by
        // LM-Kit — writing it anyway would make the property disagree with what is rendered.
        if (delivery == SystemPromptDelivery.Property)
            chat.SystemPrompt = systemPrompt;

        // Registered BEFORE the caller's first Submit, which both paths need for different
        // reasons: on an empty history it is what makes LM-Kit render the catalog at all; on a
        // seeded history it is what lets LM-Kit PARSE and INVOKE the calls the seeded catalog
        // invites (that half was never gated on MessageCount — only on Tools.Count > 0).
        // overwrite:true so a caller that also registers the same tools stays a no-op instead of
        // throwing InvalidOperationException on the duplicate name.
        if (tools is { Count: > 0 })
        {
            foreach (var tool in tools) chat.Tools.Register(tool, overwrite: true);
        }

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
