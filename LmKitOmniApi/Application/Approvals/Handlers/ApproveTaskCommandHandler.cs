using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.Approvals.Commands;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;

namespace LmKitOmniApi.Application.Approvals.Handlers;

public class ApproveTaskCommandHandler : IRequestHandler<ApproveTaskCommand, ApproveTaskResult>
{
    private readonly HermesDbContext _dbContext;
    private readonly IAgentOrchestrator _agentOrchestrator;
    private readonly TaskApprovalPayloadProtector _payloadProtector;
    private readonly ILogger<ApproveTaskCommandHandler> _logger;

    public ApproveTaskCommandHandler(
        HermesDbContext dbContext,
        IAgentOrchestrator agentOrchestrator,
        TaskApprovalPayloadProtector payloadProtector,
        ILogger<ApproveTaskCommandHandler> logger)
    {
        _dbContext = dbContext;
        _agentOrchestrator = agentOrchestrator;
        _payloadProtector = payloadProtector;
        _logger = logger;
    }

    public async Task<ApproveTaskResult> Handle(ApproveTaskCommand request, CancellationToken cancellationToken)
    {
        var task = await _dbContext.TaskApprovals
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TaskId
                && t.TenantId == request.TenantId
                && t.UserId == request.UserId, cancellationToken);
        if (task == null) return new ApproveTaskResult { Outcome = ApproveTaskOutcome.NotFound };

        // Atomically claim the task. Two concurrent approval requests must never
        // execute the same side-effecting tool twice.
        var claimed = await _dbContext.TaskApprovals
            .Where(t => t.Id == request.TaskId
                && t.TenantId == request.TenantId
                && t.UserId == request.UserId
                && t.Status == "Pending")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.Status, "Executing")
                .SetProperty(t => t.ResolvedAtUtc, DateTime.UtcNow),
                cancellationToken);

        if (claimed == 0)
            return new ApproveTaskResult { Outcome = ApproveTaskOutcome.Conflict };

        // Execute tool directly. The orchestrator re-checks the caller's current
        // permissions at execution time.
        string result;
        // Hoisted out of the try so the reconciler below can record the same input on
        // both paths; an unprotect failure leaves it empty and lands in the catch.
        var parameters = string.Empty;
        try
        {
            parameters = _payloadProtector.Unprotect(task.ParametersJson);
            result = await _agentOrchestrator.ExecuteDirectActionAsync(
                request.TenantId,
                request.UserId,
                task.ActionName,
                parameters,
                request.TaskId,
                cancellationToken);

            await _dbContext.TaskApprovals
                .Where(t => t.Id == request.TaskId && t.Status == "Executing")
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(t => t.Status, "Completed"),
                    cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing approved task.");
            await _dbContext.TaskApprovals
                .Where(t => t.Id == request.TaskId && t.Status == "Executing")
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(t => t.Status, "Failed"),
                    cancellationToken);
            await ReconcileAgentRunAsync(
                () => AgentRunApprovalReconciler.RecordFailedExecutionAsync(
                    _dbContext, task, parameters, cancellationToken),
                request.TaskId);
            return new ApproveTaskResult { Outcome = ApproveTaskOutcome.Failed };
        }

        // An agent run parked on this approval is otherwise stuck in
        // "AwaitingApproval" forever: record the call it was waiting on as a real
        // step and move the run to a terminal state. No-ops for a chat approval.
        await ReconcileAgentRunAsync(
            () => AgentRunApprovalReconciler.RecordApprovedExecutionAsync(
                _dbContext, task, parameters, result, cancellationToken),
            request.TaskId);

        return new ApproveTaskResult { Outcome = ApproveTaskOutcome.Completed, Result = result };
    }

    /// <summary>
    /// Runs the agent-run reconciliation without letting it change the approval's
    /// outcome. The tool has already run (or already failed) and the approval row is
    /// already resolved by this point, so a bookkeeping failure must not turn a
    /// successful approval into a 500 that invites a pointless retry.
    /// </summary>
    private async Task ReconcileAgentRunAsync(Func<Task<bool>> reconcile, Guid taskId)
    {
        try
        {
            await reconcile();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to reconcile the agent run parked on approval {TaskId}; the run may stay in AwaitingApproval.",
                taskId);
        }
    }
}
