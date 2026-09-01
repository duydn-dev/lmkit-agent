using LmKitOmniApi.Application.AgentRuns.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.AgentRuns.Handlers;

public sealed class GetAgentRunQueryHandler : IRequestHandler<GetAgentRunQuery, AgentRunDetailDto?>
{
    private readonly HermesDbContext _dbContext;

    public GetAgentRunQueryHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<AgentRunDetailDto?> Handle(GetAgentRunQuery request, CancellationToken cancellationToken)
    {
        return await _dbContext.AgentRuns
            .AsNoTracking()
            .Where(run => run.Id == request.RunId
                && run.TenantId == request.TenantId
                && run.UserId == request.UserId)
            .Select(run => new AgentRunDetailDto
            {
                Id = run.Id,
                Goal = run.Goal,
                Status = run.Status,
                Result = run.Result,
                Error = run.Error,
                CreatedAtUtc = run.CreatedAtUtc,
                CompletedAtUtc = run.CompletedAtUtc,
                Steps = run.Steps
                    .OrderBy(step => step.Ordinal)
                    .Select(step => new AgentRunStepDto
                    {
                        Ordinal = step.Ordinal,
                        Action = step.Action,
                        Input = step.Input,
                        Observation = step.Observation,
                        CreatedAtUtc = step.CreatedAtUtc
                    })
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);
    }
}
