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
        // Materialize first (the payload is decrypted in memory — EF can't translate
        // the protector), then decrypt each owner-scoped payload for display.
        var rows = await _dbContext.TaskApprovals
            .Where(t => t.TenantId == request.TenantId && t.UserId == request.UserId && t.Status == "Pending")
            .OrderByDescending(t => t.CreatedAtUtc)
            .Select(t => new { t.Id, t.ActionName, t.ParametersJson, t.CreatedAtUtc })
            .ToListAsync(cancellationToken);

        return rows.Select(t => new PendingApprovalDto
        {
            Id = t.Id,
            ActionName = t.ActionName,
            Details = Describe(t.ParametersJson),
            CreatedAtUtc = t.CreatedAtUtc
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
