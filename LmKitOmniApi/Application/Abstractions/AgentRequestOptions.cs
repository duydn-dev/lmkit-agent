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
}
