using LmKitOmniApi.Application.AgentRuns.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Common;

namespace LmKitOmniApi.Application.AgentRuns.Handlers;

public sealed class GetAgentRunsQueryHandler : IRequestHandler<GetAgentRunsQuery, Common.PagedResult<AgentRunSummaryDto>>
{
    private readonly HermesDbContext _dbContext;

    public GetAgentRunsQueryHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<Common.PagedResult<AgentRunSummaryDto>> Handle(GetAgentRunsQuery request, CancellationToken cancellationToken)
    {
        var query = _dbContext.AgentRuns
            .AsNoTracking()
            .Where(run => run.TenantId == request.TenantId && run.UserId == request.UserId);

        var search = request.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search}%";
            query = query.Where(run => EF.Functions.Like(run.Goal, pattern));
        }

        return await query
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
            .ToPagedResultAsync(request.Page, request.PageSize, cancellationToken);
    }
}
