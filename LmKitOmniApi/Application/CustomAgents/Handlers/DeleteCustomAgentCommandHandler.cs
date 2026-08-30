using LmKitOmniApi.Application.CustomAgents.Commands;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.CustomAgents.Handlers;

public class DeleteCustomAgentCommandHandler : IRequestHandler<DeleteCustomAgentCommand, bool>
{
    private readonly HermesDbContext _dbContext;

    public DeleteCustomAgentCommandHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> Handle(DeleteCustomAgentCommand request, CancellationToken cancellationToken)
    {
        // Owner-only; missing and not-owned both yield false (→ 404, never 403).
        var agent = await _dbContext.CustomAgents.FirstOrDefaultAsync(
            candidate => candidate.Id == request.AgentId
                && candidate.TenantId == request.TenantId
                && candidate.OwnerUserId == request.UserId,
            cancellationToken);
        if (agent is null) return false;

        // Sessions referencing the agent fall back automatically: the
        // ChatSession.CustomAgentId FK is configured with DeleteBehavior.SetNull.
        _dbContext.CustomAgents.Remove(agent);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
