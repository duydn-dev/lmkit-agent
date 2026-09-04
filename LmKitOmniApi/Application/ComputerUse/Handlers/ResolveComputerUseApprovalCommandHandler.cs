using LmKitOmniApi.Application.ComputerUse.Commands;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.ComputerUse.Handlers;

/// <summary>
/// Records a human decision on a pending computer-use approval. Security-critical
/// semantics: the row is matched owner-scoped (tenant + user) AND restricted to
/// <c>ActionName == "COMPUTER_USE"</c> so this path can only ever resolve computer-use
/// approvals, never other tools' approvals; the flip is an atomic Pending→terminal claim
/// so two concurrent resolutions can't both win.
/// </summary>
public sealed class ResolveComputerUseApprovalCommandHandler
    : IRequestHandler<ResolveComputerUseApprovalCommand, ResolveComputerUseApprovalOutcome>
{
    private const int MaxCommentChars = 1024;

    private readonly HermesDbContext _dbContext;

    public ResolveComputerUseApprovalCommandHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<ResolveComputerUseApprovalOutcome> Handle(
        ResolveComputerUseApprovalCommand request, CancellationToken cancellationToken)
    {
        var exists = await _dbContext.TaskApprovals
            .AsNoTracking()
            .AnyAsync(t => t.Id == request.ApprovalId
                && t.TenantId == request.TenantId
                && t.UserId == request.UserId
                && t.ActionName == "COMPUTER_USE", cancellationToken);
        if (!exists) return ResolveComputerUseApprovalOutcome.NotFound;

        var newStatus = request.Approve ? "Approved" : "Rejected";
        var comment = request.Approve
            ? null
            : (request.Comment is { Length: > MaxCommentChars } c ? c[..MaxCommentChars] : request.Comment);

        var claimed = await _dbContext.TaskApprovals
            .Where(t => t.Id == request.ApprovalId
                && t.TenantId == request.TenantId
                && t.UserId == request.UserId
                && t.ActionName == "COMPUTER_USE"
                && t.Status == "Pending")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.Status, newStatus)
                .SetProperty(t => t.RejectionComment, comment)
                .SetProperty(t => t.ResolvedAtUtc, DateTime.UtcNow),
                cancellationToken);

        return claimed == 0 ? ResolveComputerUseApprovalOutcome.Conflict : ResolveComputerUseApprovalOutcome.Resolved;
    }
}
