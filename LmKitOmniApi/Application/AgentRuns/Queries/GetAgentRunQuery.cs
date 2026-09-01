using MediatR;

namespace LmKitOmniApi.Application.AgentRuns.Queries;

/// <summary>Returns one agent run with its ordered steps, scoped to the caller.</summary>
public sealed class GetAgentRunQuery : IRequest<AgentRunDetailDto?>
{
    public Guid RunId { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
}

public sealed class AgentRunDetailDto
{
    public Guid Id { get; set; }
    public string Goal { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Result { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public List<AgentRunStepDto> Steps { get; set; } = new();
}

public sealed class AgentRunStepDto
{
    public int Ordinal { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Input { get; set; } = string.Empty;
    public string Observation { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
