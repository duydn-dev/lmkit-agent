using MediatR;

namespace LmKitOmniApi.Application.AgentRuns.Commands;

/// <summary>
/// Starts a goal-oriented autonomous agent run and streams its progress
/// (the same SSE marker channel as chat, plus [STEP:] tool-step markers). The
/// handler creates a hidden agent-run chat session + an AgentRun row, drives the
/// ReAct orchestrator toward the goal, persists each tool step and the final
/// result, and yields the stream. TenantId/UserId are set by the controller from
/// the authenticated principal.
/// </summary>
public sealed class StreamAgentRunCommand : IStreamRequest<string>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Goal { get; set; } = string.Empty;

    /// <summary>Set by the handler once the AgentRun row exists, so the controller can echo it first.</summary>
    public Guid RunId { get; set; }
}
