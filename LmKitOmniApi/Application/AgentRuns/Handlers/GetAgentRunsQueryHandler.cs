using LmKitOmniApi.Application.AgentRuns.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.AgentRuns.Handlers;

public sealed class GetAgentRunsQueryHandler : IRequestHandler<GetAgentRunsQuery, List<AgentRunSummaryDto>>
{
    private readonly HermesDbContext _dbContext;

    public GetAgentRunsQueryHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<List<AgentRunSummaryDto>> Handle(GetAgentRunsQuery request, CancellationToken cancellationToken)
    {
        return await _dbContext.AgentRuns
            .AsNoTracking()
            .Where(run => run.TenantId == request.TenantId && run.UserId == request.UserId)
            .OrderByDescending(run => run.CreatedAtUtc)
            .Select(run => new AgentRunSummaryDto
            {
                Id = run.Id,
                Goal = run.Goal,
                Status = run.Status,
                StepCount = run.Steps.Count,
                CreatedAtUtc = run.CreatedAtUtc,
                CompletedAtUtc = run.CompletedAtUtc
            })
            .ToListAsync(cancellationToken);
    }
}
