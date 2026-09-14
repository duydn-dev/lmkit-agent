using MediatR;

namespace LmKitOmniApi.Application.AgentRuns.Queries;

/// <summary>Getlist chuẩn: run của người gọi, mới nhất trước, phân trang + tìm theo mục tiêu.</summary>
public sealed class GetAgentRunsQuery : IRequest<Common.PagedResult<AgentRunSummaryDto>>
{
    public Guid TenantId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = Common.Paging.DefaultPageSize;
    public string? Search { get; set; }
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
