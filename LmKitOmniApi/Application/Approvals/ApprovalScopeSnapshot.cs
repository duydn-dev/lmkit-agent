using System.Text.Json;
using System.Text.Json.Serialization;
using LmKitOmniApi.Application.Abstractions;

namespace LmKitOmniApi.Application.Approvals;

/// <summary>
/// Captures and replays the NARROWING half of a requesting turn's
/// <see cref="AgentRequestOptions"/> onto a <c>TaskApproval</c> row, so that approving a
/// human-in-the-loop action can never grant more authority than the turn that asked for it.
///
/// <para><b>What it stores, and what it deliberately does not.</b> Only the three fields
/// that RESTRICT execution: <see cref="AgentRequestOptions.AllowedTools"/>,
/// <see cref="AgentRequestOptions.KnowledgeDocumentIds"/> and
/// <see cref="AgentRequestOptions.AllowWebSearch"/> — precisely the three the action
/// dispatcher consults. <c>PersonaPrompt</c>, <c>ShowReasoning</c> and
/// <c>LoraAdapterId</c> shape a MODEL call; an approved action is a direct tool
/// invocation with no inference, so replaying them would persist prompt text (and a
/// persona is user-authored content, which is the sort of thing this column is careful
/// not to hold) for no effect.</para>
///
/// <para><b>Why a snapshot at all.</b> The scope used to be recovered exclusively by
/// walking approval → <c>ChatSessionId</c> → session → bound <c>CustomAgentId</c> → custom
/// agent. Deleting the agent NULLs the session's binding, so that walk then reports
/// "unbound" — and unbound means unscoped, i.e. the approval executed with strictly MORE
/// authority than the turn that requested it. A row-local snapshot cannot be erased by a
/// delete somewhere else.</para>
///
/// <para><b>Why the snapshot does not simply win.</b> A snapshot can go stale in the
/// other direction: the agent's whitelist may have been narrowed, or a pinned document
/// deleted, while the approval sat pending. The resolver therefore takes the INTERSECTION
/// of the snapshot and the live walk (<see cref="Narrow"/>), which is ≤ both. So the
/// result is never wider than the requesting turn (bounded by the snapshot) and never
/// wider than the agent's current setting (bounded by the walk) — the safe direction is
/// the default in both.</para>
/// </summary>
public static class ApprovalScopeSnapshot
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// The stored shape. A tiny explicit DTO rather than <see cref="AgentRequestOptions"/>
    /// itself, so adding a field to that record can never start silently persisting it.
    /// Nullable collections carry the same meaning they do on the options record:
    /// <c>null</c> = "no restriction of this kind", empty = "deny everything".
    /// </summary>
    internal sealed record Payload
    {
        public IReadOnlyCollection<string>? AllowedTools { get; init; }
        public IReadOnlyCollection<Guid>? KnowledgeDocumentIds { get; init; }
        public bool AllowWebSearch { get; init; } = true;
    }

    /// <summary>
    /// Serializes the narrowing fields of <paramref name="options"/> for storage on the
    /// approval row, or returns <c>null</c> when there is nothing to narrow.
    ///
    /// <para>Null is returned for a genuinely unscoped turn on purpose: it makes an
    /// unscoped turn and a pre-migration row indistinguishable, and they must behave
    /// identically — both fall back to the session walk, which for an unbound session
    /// yields unscoped, which is what that turn actually had.</para>
    /// </summary>
    public static string? Capture(AgentRequestOptions? options)
    {
        if (options is null) return null;

        var payload = new Payload
        {
            AllowedTools = options.AllowedTools,
            KnowledgeDocumentIds = options.KnowledgeDocumentIds,
            AllowWebSearch = options.AllowWebSearch
        };

        // Nothing restrictive to record: no whitelist, no document pin, and web search
        // left on. Storing that is indistinguishable from storing nothing, and storing
        // nothing keeps the fallback path identical to the pre-snapshot behaviour.
        if (payload.AllowedTools is null && payload.KnowledgeDocumentIds is null && payload.AllowWebSearch)
            return null;

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    /// <summary>
    /// Reads a stored snapshot back.
    /// </summary>
    /// <returns>
    /// <c>true</c> with <paramref name="options"/> set when a snapshot was present and
    /// readable; <c>true</c> with <c>null</c> when there was no snapshot at all;
    /// <c>false</c> when a snapshot IS present but cannot be parsed — the caller must then
    /// fail closed rather than treat corruption as "unscoped".
    /// </returns>
    public static bool TryRead(string? json, out AgentRequestOptions? options)
    {
        options = null;
        if (string.IsNullOrWhiteSpace(json)) return true;

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is null) return false;

        options = new AgentRequestOptions
        {
            AllowedTools = payload.AllowedTools,
            KnowledgeDocumentIds = payload.KnowledgeDocumentIds,
            AllowWebSearch = payload.AllowWebSearch
        };
        return true;
    }

    /// <summary>The deny-everything scope used whenever a scope is known to exist but cannot be read.</summary>
    public static AgentRequestOptions DenyAll { get; } = new()
    {
        AllowWebSearch = false,
        AllowedTools = Array.Empty<string>()
    };

    /// <summary>
    /// Combines two scopes into the one that is no wider than EITHER of them.
    ///
    /// <para>For each collection, <c>null</c> means "unrestricted", so the result is the
    /// other side when one is null and the set intersection when both are present.
    /// <c>AllowWebSearch</c> is the conjunction. Consequently the result is a subset of
    /// both inputs on every axis; a deny-all on either side makes the whole thing
    /// deny-all. Returns <c>null</c> only when BOTH sides are null, i.e. nothing narrows
    /// this execution at all.</para>
    ///
    /// <para><b>The one place set intersection is not enough.</b> An EMPTY tool whitelist
    /// means "no tools", but an EMPTY document allowlist means "no restriction" —
    /// <c>RagPipelineService</c> only applies the pin when <c>documentIds is { Count: > 0 }</c>.
    /// So two DISJOINT document pins cannot be expressed as their intersection: writing
    /// the empty set would widen retrieval from "the agent's pinned document" to the whole
    /// tenant knowledge base, which is the precise leak this class exists to prevent.
    /// There is no scope that is ≤ both and still reads anything, so this case answers
    /// <see cref="DenyAll"/> — the same answer the resolver already gives for the other
    /// "the requesting turn WAS scoped and that scope is not reconstructible" case. The
    /// deliberate cost: a non-RAG action approved under an agent whose document pins were
    /// entirely replaced while it sat pending is refused rather than run. Rare, visible to
    /// the user as a refusal, and recoverable by asking again — unlike a silent
    /// widening.</para>
    /// </summary>
    public static AgentRequestOptions? Narrow(AgentRequestOptions? left, AgentRequestOptions? right)
    {
        if (left is null) return right;
        if (right is null) return left;

        var documents = Intersect(left.KnowledgeDocumentIds, right.KnowledgeDocumentIds);
        if (documents is { Count: 0 }) return DenyAll;

        return new AgentRequestOptions
        {
            AllowWebSearch = left.AllowWebSearch && right.AllowWebSearch,
            AllowedTools = IntersectTools(left.AllowedTools, right.AllowedTools),
            KnowledgeDocumentIds = documents
        };
    }

    /// <summary>Tool names are compared case-insensitively, exactly like the dispatcher's whitelist gate.</summary>
    private static IReadOnlyCollection<string>? IntersectTools(
        IReadOnlyCollection<string>? left, IReadOnlyCollection<string>? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left.Intersect(right, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyCollection<Guid>? Intersect(
        IReadOnlyCollection<Guid>? left, IReadOnlyCollection<Guid>? right)
    {
        // An empty pin already means "no restriction" to RagPipelineService, so normalize
        // it to null here. Without this, the emptiness of an INPUT would be read by
        // Narrow as an empty INTERSECTION and refuse an execution that was never
        // document-scoped in the first place.
        if (left is { Count: 0 }) left = null;
        if (right is { Count: 0 }) right = null;
        if (left is null) return right;
        if (right is null) return left;
        return left.Intersect(right).ToArray();
    }
}
