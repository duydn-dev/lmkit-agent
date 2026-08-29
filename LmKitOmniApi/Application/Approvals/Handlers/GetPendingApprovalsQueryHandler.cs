using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Approvals.Queries;
using LmKitOmniApi.Infrastructure.Data;

namespace LmKitOmniApi.Application.Approvals.Handlers;

public class GetPendingApprovalsQueryHandler : IRequestHandler<GetPendingApprovalsQuery, List<PendingApprovalDto>>
{
    private readonly HermesDbContext _dbContext;

    public GetPendingApprovalsQueryHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<PendingApprovalDto>> Handle(GetPendingApprovalsQuery request, CancellationToken cancellationToken)
    {
        return await _dbContext.TaskApprovals
            .Where(t => t.TenantId == request.TenantId && t.UserId == request.UserId && t.Status == "Pending")
            .OrderByDescending(t => t.CreatedAtUtc)
            .Select(t => new PendingApprovalDto
            {
                Id = t.Id,
                ActionName = t.ActionName,
                CreatedAtUtc = t.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);
    }
}
