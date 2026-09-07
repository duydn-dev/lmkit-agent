using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Approvals.Commands;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;

namespace LmKitOmniApi.Application.Approvals.Handlers;

/// <summary>
/// <b>Deliberately has no expiry guard, unlike <see cref="ApproveTaskCommandHandler"/>.</b>
/// The deadline exists to stop a stale, out-of-context action from EXECUTING; a rejection
/// executes nothing. Refusing to reject an overdue-but-unswept approval would only leave
/// the row and the run parked for the sweeper while discarding a decision the human
/// actually made — strictly worse. So a reject that lands after the deadline but before
/// the sweep still succeeds, records the human's refusal, and terminates the run as
/// Rejected, which is more truthful than Expired: somebody did look.
///
/// <para>Once the sweeper has run, the row is <c>Expired</c> rather than <c>Pending</c>,
/// the claim below matches nothing, and the caller gets the existing 404 — the same answer
/// as rejecting an already-approved task.</para>
/// </summary>
public class RejectTaskCommandHandler : IRequestHandler<RejectTaskCommand, bool>
{
    private readonly HermesDbContext _dbContext;
    private readonly TaskApprovalPayloadProtector _payloadProtector;
    private readonly ILogger<RejectTaskCommandHandler> _logger;

    public RejectTaskCommandHandler(
        HermesDbContext dbContext,
        TaskApprovalPayloadProtector payloadProtector,
        ILogger<RejectTaskCommandHandler> logger)
    {
        _dbContext = dbContext;
        _payloadProtector = payloadProtector;
        _logger = logger;
    }

    public async Task<bool> Handle(RejectTaskCommand request, CancellationToken cancellationToken)
    {
        // Read before the claim so the rejection can be reflected on the agent run
        // parked on this approval. Owner-scoped exactly like the update below, so a
        // foreign id still reveals nothing.
        var task = await _dbContext.TaskApprovals
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TaskId
                && t.TenantId == request.TenantId
                && t.UserId == request.UserId, cancellationToken);
        if (task == null) return false;

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

        if (rejected == 0) return false;

        // An agent run parked on this approval is otherwise stuck in
        // "AwaitingApproval" forever: record the refusal as a step and move the run to
        // a terminal state. No-ops for a chat approval. Bookkeeping must never turn a
        // successful rejection into a 404 — the approval row is already resolved.
        try
        {
            await AgentRunApprovalReconciler.RecordRejectionAsync(
                _dbContext, task, ReadParameters(task.ParametersJson), request.Comment, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to reconcile the agent run parked on approval {TaskId}; the run may stay in AwaitingApproval.",
                request.TaskId);
        }

        return true;
    }

    /// <summary>
    /// Best-effort decrypt of the stored payload for the recorded step's input. A
    /// payload that no longer unprotects (rotated keys) must not fail the rejection.
    /// </summary>
    private string ReadParameters(string parametersJson)
    {
        try
        {
            return _payloadProtector.Unprotect(parametersJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not decrypt a rejected approval payload; recording an empty step input.");
            return string.Empty;
        }
    }
}
