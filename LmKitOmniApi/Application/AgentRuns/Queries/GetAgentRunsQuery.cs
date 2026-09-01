using MediatR;

namespace LmKitOmniApi.Application.AgentRuns.Queries;

/// <summary>Lists the caller's own agent runs, newest first.</summary>
public sealed class GetAgentRunsQuery : IRequest<List<AgentRunSummaryDto>>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
}

public sealed class AgentRunSummaryDto
{
    public Guid Id { get; set; }
    public string Goal { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int StepCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}
