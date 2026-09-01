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
        try
        {
            var parameters = _payloadProtector.Unprotect(task.ParametersJson);
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
            return new ApproveTaskResult { Outcome = ApproveTaskOutcome.Failed };
        }

        return new ApproveTaskResult { Outcome = ApproveTaskOutcome.Completed, Result = result };
    }
}
