namespace LmKitOmniApi.Application.Abstractions;

/// <summary>
/// Per-request execution options callers pass to the agent orchestrator.
/// Carries user-facing switches (tool toggles) without widening the method
/// signature every time a new toggle is added.
/// </summary>
public sealed record AgentRequestOptions
{
    /// <summary>
    /// When false the web-search tool is neither offered to the ReAct planner
    /// nor executable through action dispatch for this request.
    /// </summary>
    public bool AllowWebSearch { get; init; } = true;

    /// <summary>
    /// Custom-agent persona injected into the system prompt for this request.
    /// Null/empty = the default assistant persona.
    /// </summary>
    public string? PersonaPrompt { get; init; }

    /// <summary>
    /// Tool-name whitelist for this request. Null = the caller role's default
    /// tool set; non-null = intersection with that set (never a widening).
    /// Uses the permission tool names (e.g. "SearchWeb", "QueryKnowledgeBase").
    /// </summary>
    public IReadOnlyCollection<string>? AllowedTools { get; init; }

    /// <summary>
    /// When non-empty, RAG retrieval for this request is restricted to these
    /// document ids (already validated as accessible to the caller).
    /// </summary>
    public IReadOnlyCollection<Guid>? KnowledgeDocumentIds { get; init; }
}
