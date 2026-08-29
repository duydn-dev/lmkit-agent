using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Approvals.Commands;
using LmKitOmniApi.Infrastructure.Data;

namespace LmKitOmniApi.Application.Approvals.Handlers;

public class RejectTaskCommandHandler : IRequestHandler<RejectTaskCommand, bool>
{
    private readonly HermesDbContext _dbContext;

    public RejectTaskCommandHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> Handle(RejectTaskCommand request, CancellationToken cancellationToken)
    {
        var rejected = await _dbContext.TaskApprovals
            .Where(t => t.Id == request.TaskId
                && t.TenantId == request.TenantId
                && t.UserId == request.UserId
                && t.Status == "Pending")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.Status, "Rejected")
                .SetProperty(t => t.ResolvedAtUtc, DateTime.UtcNow)
                .SetProperty(t => t.RejectionComment, request.Comment),
                cancellationToken);

        return rejected > 0;
    }
}
