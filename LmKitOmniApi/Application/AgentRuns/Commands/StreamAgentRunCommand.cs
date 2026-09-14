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

    /// <summary>
    /// Persona tùy chọn: id một CustomAgent mà NGƯỜI CHẠY dùng được (của mình hoặc
    /// chia sẻ tenant). Handler resolve và áp persona + tool whitelist + knowledge +
    /// LoRA của agent lên run — đúng như một phiên chat với agent đó. Agent không còn
    /// truy cập được → chạy KHÔNG persona (soft reference, không gãy lịch).
    /// </summary>
    public Guid? CustomAgentId { get; set; }

    /// <summary>Set by the handler once the AgentRun row exists, so the controller can echo it first.</summary>
    public Guid RunId { get; set; }
}
