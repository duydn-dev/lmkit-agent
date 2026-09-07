using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.AgentRuns;
using LmKitOmniApi.Application.Approvals.Commands;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;

namespace LmKitOmniApi.Application.Approvals.Handlers;

public class ApproveTaskCommandHandler : IRequestHandler<ApproveTaskCommand, ApproveTaskResult>
{
    private readonly HermesDbContext _dbContext;
    private readonly IAgentOrchestrator _agentOrchestrator;
    private readonly TaskApprovalPayloadProtector _payloadProtector;
    private readonly ILogger<ApproveTaskCommandHandler> _logger;

    /// <summary>
    /// The continuation worker's presence in this process, or <c>null</c> when the host
    /// never registered one. OPTIONAL with a <c>null</c> default on purpose: a run must
    /// only be marked for a continuation somebody will actually drive, and a
    /// configuration flag cannot establish that — it reads the same whether or not
    /// <c>AddAgentRunResume</c> was ever called, so queuing on its say-so could strand a
    /// run in exactly the way this whole change exists to prevent. Null here reproduces
    /// the pre-resume behaviour exactly.
    /// </summary>
    private readonly AgentRunResumeQueue? _resumeQueue;

    public ApproveTaskCommandHandler(
        HermesDbContext dbContext,
        IAgentOrchestrator agentOrchestrator,
        TaskApprovalPayloadProtector payloadProtector,
        ILogger<ApproveTaskCommandHandler> logger,
        AgentRunResumeQueue? resumeQueue = null)
    {
        _dbContext = dbContext;
        _agentOrchestrator = agentOrchestrator;
        _payloadProtector = payloadProtector;
        _logger = logger;
        _resumeQueue = resumeQueue;
    }

    public async Task<ApproveTaskResult> Handle(ApproveTaskCommand request, CancellationToken cancellationToken)
    {
        var task = await _dbContext.TaskApprovals
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TaskId
                && t.TenantId == request.TenantId
                && t.UserId == request.UserId, cancellationToken);
        if (task == null) return new ApproveTaskResult { Outcome = ApproveTaskOutcome.NotFound };

        var now = DateTime.UtcNow;

        // A stale approval must never execute. The payload was captured against a
        // situation that has since moved on, and a human clicking "approve" on an old row
        // is approving something whose context is gone — so refuse before the claim and
        // say why, instead of letting it read as a lost race.
        if (task.Status == "Pending" && task.ExpiresAtUtc <= now)
            return new ApproveTaskResult { Outcome = ApproveTaskOutcome.Expired };

        // A computer-use approval is a HITL marker, not a tool call: the action runs in the
        // computer-use loop's own browser container and that loop is what polls this row.
        // Sending it through ExecuteDirectActionAsync below could only fail (no role grants
        // a "COMPUTER_USE" tool permission), the catch would write Failed, and the gate
        // reads Failed as a REJECTION — so clicking "approve" used to reject the action.
        // Record the decision instead, which is all the gate ever needed.
        if (string.Equals(task.ActionName, TaskApproval.ComputerUseActionName, StringComparison.Ordinal))
            return await RecordComputerUseApprovalAsync(request, now, cancellationToken);

        // Atomically claim the task. Two concurrent approval requests must never
        // execute the same side-effecting tool twice. The deadline is repeated inside the
        // claim on purpose: the check above is a read that raced the clock, and the
        // background sweeper may not be running at all — this predicate is what actually
        // makes execution-after-expiry impossible.
        var claimed = await _dbContext.TaskApprovals
            .Where(t => t.Id == request.TaskId
                && t.TenantId == request.TenantId
                && t.UserId == request.UserId
                && t.Status == "Pending"
                && t.ExpiresAtUtc > now)
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
        // "AwaitingApproval" forever: record the call it was waiting on as a real step,
        // then either hand the run back to its ReAct loop with that observation or close
        // it truthfully. No-ops for a chat approval.
        var resolution = await ReconcileAgentRunAsync(
            () => AgentRunApprovalReconciler.RecordApprovedExecutionAsync(
                _dbContext, task, parameters, result, _resumeQueue?.Options, cancellationToken),
            request.TaskId);

        // Wake the worker rather than let it wait out a poll. Purely an optimisation:
        // the queue is the ResumeState column, so a missed nudge (another replica, a
        // busy worker) costs latency, never the continuation itself.
        if (resolution == AgentRunResolution.QueuedForResume) _resumeQueue?.Notify();

        return new ApproveTaskResult { Outcome = ApproveTaskOutcome.Completed, Result = result };
    }

    /// <summary>
    /// Records a YES on a computer-use approval and returns, executing nothing here.
    ///
    /// <para>The claim is the same atomic Pending→terminal transition every other
    /// resolution path uses — including the deadline predicate, and the same
    /// <c>ActionName</c> restriction <c>ResolveComputerUseApprovalCommandHandler</c>
    /// applies — so this endpoint and the dedicated one cannot both win, and neither can
    /// re-open a row the gate already timed out (that row is no longer Pending). No agent
    /// run is reconciled: a computer-use session is its own hidden substrate and never has
    /// an <c>AgentRun</c> parked on it.</para>
    /// </summary>
    private async Task<ApproveTaskResult> RecordComputerUseApprovalAsync(
        ApproveTaskCommand request, DateTime now, CancellationToken cancellationToken)
    {
        var claimed = await _dbContext.TaskApprovals
            .Where(t => t.Id == request.TaskId
                && t.TenantId == request.TenantId
                && t.UserId == request.UserId
                && t.ActionName == TaskApproval.ComputerUseActionName
                && t.Status == "Pending"
                && t.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.Status, "Approved")
                .SetProperty(t => t.ResolvedAtUtc, DateTime.UtcNow),
                cancellationToken);

        if (claimed == 0)
            return new ApproveTaskResult { Outcome = ApproveTaskOutcome.Conflict };

        _logger.LogInformation(
            "Recorded a computer-use approval for {TaskId}; the waiting loop performs the action.", request.TaskId);
        return new ApproveTaskResult
        {
            Outcome = ApproveTaskOutcome.Recorded,
            Result = "Đã ghi nhận phê duyệt. Phiên computer-use sẽ thực hiện hành động này."
        };
    }

    /// <summary>
    /// Runs the agent-run reconciliation without letting it change the approval's
    /// outcome. The tool has already run (or already failed) and the approval row is
    /// already resolved by this point, so a bookkeeping failure must not turn a
    /// successful approval into a 500 that invites a pointless retry.
    /// </summary>
    /// <returns>
    /// What happened to the run, or <see cref="AgentRunResolution.NoParkedRun"/> when the
    /// reconciliation itself failed — a failure means nothing was queued, so the caller
    /// must not go on to nudge a worker about work that does not exist.
    /// </returns>
    private async Task<AgentRunResolution> ReconcileAgentRunAsync(
        Func<Task<AgentRunResolution>> reconcile, Guid taskId)
    {
        try
        {
            return await reconcile();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to reconcile the agent run parked on approval {TaskId}; the run may stay in AwaitingApproval.",
                taskId);
            return AgentRunResolution.NoParkedRun;
        }
    }
}
