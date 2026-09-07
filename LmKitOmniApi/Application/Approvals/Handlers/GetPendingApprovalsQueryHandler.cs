using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Approvals.Queries;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;

namespace LmKitOmniApi.Application.Approvals.Handlers;

public class GetPendingApprovalsQueryHandler : IRequestHandler<GetPendingApprovalsQuery, List<PendingApprovalDto>>
{
    private const int MaxDetailsChars = 4000;

    private readonly HermesDbContext _dbContext;
    private readonly TaskApprovalPayloadProtector _payloadProtector;

    public GetPendingApprovalsQueryHandler(HermesDbContext dbContext, TaskApprovalPayloadProtector payloadProtector)
    {
        _dbContext = dbContext;
        _payloadProtector = payloadProtector;
    }

    public async Task<List<PendingApprovalDto>> Handle(GetPendingApprovalsQuery request, CancellationToken cancellationToken)
    {
        // Defence in depth on the deadline: an overdue row is filtered out here even
        // though the sweeper normally flips its Status to Expired first. The sweeper can
        // be disabled, behind, or down, and this list is what a human picks from — it must
        // never offer an action the approve endpoint would refuse anyway. It is also what
        // stops the list growing without bound from approvals nobody ever answered.
        var now = DateTime.UtcNow;
        var rows = await _dbContext.TaskApprovals
            .Where(t => t.TenantId == request.TenantId
                && t.UserId == request.UserId
                && t.Status == "Pending"
                && t.ExpiresAtUtc > now)
            .OrderByDescending(t => t.CreatedAtUtc)
            .Select(t => new { t.Id, t.ActionName, t.ParametersJson, t.CreatedAtUtc, t.ExpiresAtUtc })
            .ToListAsync(cancellationToken);

        return rows.Select(t => new PendingApprovalDto
        {
            Id = t.Id,
            ActionName = t.ActionName,
            Details = Describe(t.ParametersJson),
            CreatedAtUtc = t.CreatedAtUtc,
            ExpiresAtUtc = t.ExpiresAtUtc
        }).ToList();
    }

    private string Describe(string parametersJson)
    {
        if (string.IsNullOrEmpty(parametersJson)) return string.Empty;
        string decrypted;
        try { decrypted = _payloadProtector.Unprotect(parametersJson); }
        catch { return string.Empty; } // never surface a decrypt failure as content
        return decrypted.Length > MaxDetailsChars ? decrypted[..MaxDetailsChars] + "…" : decrypted;
    }
}
