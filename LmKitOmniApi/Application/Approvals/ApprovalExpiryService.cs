using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Application.Approvals;

/// <summary>
/// Moves overdue <see cref="TaskApproval"/> rows to <see cref="TaskApproval.ExpiredStatus"/>
/// and releases the <see cref="Domain.Entities.AgentRun"/> each one had parked.
///
/// <para><b>Why a sweeper and not just a query filter.</b> Refusing a stale approval at the
/// API (which <c>ApproveTaskCommandHandler</c> and <c>GetPendingApprovalsQueryHandler</c>
/// also do) protects the human. It does nothing for the run: a run only leaves
/// <c>AwaitingApproval</c> when something calls <c>AgentRunApprovalReconciler</c>, and an
/// approval nobody clicks never calls anything. Closing that last edge needs a process that
/// acts without a request, which is this.</para>
///
/// <para><b>Idempotent by claim.</b> Each row is claimed with the same conditional
/// <c>ExecuteUpdate</c> the approve and reject handlers use — <c>WHERE Status = 'Pending'</c>
/// — so exactly one sweep (or one human) ever transitions a given approval, and the
/// reconciliation that follows runs at most once. The reconciler's own
/// <c>AwaitingApproval</c> predicate is the second, independent guard: even a duplicated
/// call cannot step or re-score a run that already ended. Overlapping sweeps and a sweep
/// racing a human are both safe.</para>
///
/// <para><b>Tenancy.</b> The sweep is deliberately global: it acts for the system, not for a
/// caller, so there is no tenant to filter by and every tenant's backlog must drain. What
/// stays scoped is the effect — the reconciler matches a run on
/// (ChatSessionId, TenantId, UserId) taken from the row itself, so expiring tenant A's
/// approval can never touch tenant B's run.</para>
///
/// <para>Scoped: it holds a <see cref="HermesDbContext"/>. <see cref="ApprovalExpiryWorker"/>
/// creates one scope per sweep.</para>
/// </summary>
public sealed class ApprovalExpirySweeper
{
    private readonly HermesDbContext _db;
    private readonly TaskApprovalPayloadProtector _payloadProtector;
    private readonly ApprovalExpiryOptions _options;
    private readonly ILogger<ApprovalExpirySweeper> _logger;

    public ApprovalExpirySweeper(
        HermesDbContext db,
        TaskApprovalPayloadProtector payloadProtector,
        IOptions<ApprovalExpiryOptions> options,
        ILogger<ApprovalExpirySweeper> logger)
    {
        _db = db;
        _payloadProtector = payloadProtector;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Expires every approval already past its deadline at <paramref name="nowUtc"/>,
    /// in bounded batches.
    /// </summary>
    /// <returns>How many approvals this pass actually transitioned.</returns>
    public async Task<int> SweepAsync(DateTime nowUtc, CancellationToken ct)
    {
        var batchSize = _options.EffectiveBatchSize;
        var expired = 0;

        for (var batch = 0; batch < _options.EffectiveMaxBatchesPerSweep; batch++)
        {
            ct.ThrowIfCancellationRequested();

            // Read-only and paged: the WHERE narrows on the same two columns the
            // IX_task_approvals_Status_ExpiresAtUtc index covers, and rows leave the
            // predicate as they are claimed, so the next batch is the next page without
            // an offset. AsNoTracking keeps a large backlog out of the change tracker.
            var due = await _db.TaskApprovals
                .AsNoTracking()
                .Where(t => t.Status == "Pending" && t.ExpiresAtUtc <= nowUtc)
                .OrderBy(t => t.ExpiresAtUtc)
                .Take(batchSize)
                .ToListAsync(ct);

            if (due.Count == 0) break;

            foreach (var task in due)
            {
                ct.ThrowIfCancellationRequested();
                if (await ExpireAsync(task, nowUtc, ct)) expired++;
            }

            if (due.Count < batchSize) break;
        }

        if (expired > 0)
            _logger.LogInformation("Expired {Count} unanswered task approval(s).", expired);

        return expired;
    }

    private async Task<bool> ExpireAsync(TaskApproval task, DateTime nowUtc, CancellationToken ct)
    {
        // Atomic claim. A human answering this row a millisecond ago wins and this
        // returns 0 — their decision must never be overwritten by a clock.
        var claimed = await _db.TaskApprovals
            .Where(t => t.Id == task.Id && t.Status == "Pending")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.Status, TaskApproval.ExpiredStatus)
                .SetProperty(t => t.ResolvedAtUtc, nowUtc),
                ct);

        if (claimed == 0) return false;

        // The approval row is already resolved at this point, so a bookkeeping failure
        // must not abort the sweep and leave the rest of the backlog untouched. Same
        // trade-off the approve/reject handlers make: the row is authoritative, the run
        // reconciliation is best effort, and a run left behind is visible in the log.
        try
        {
            await AgentRunApprovalReconciler.RecordExpirationAsync(
                _db, task, ReadParameters(task.ParametersJson), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Expired approval {TaskId} but failed to close the agent run parked on it; "
                + "the run may stay in AwaitingApproval.",
                task.Id);
        }

        return true;
    }

    /// <summary>
    /// Best-effort decrypt of the stored payload for the recorded step's input, so the
    /// expired run's timeline shows WHAT was never approved. A payload that no longer
    /// unprotects (rotated keys) must not stop the row from expiring.
    /// </summary>
    private string ReadParameters(string parametersJson)
    {
        if (string.IsNullOrEmpty(parametersJson)) return string.Empty;
        try
        {
            return _payloadProtector.Unprotect(parametersJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not decrypt an expired approval payload; recording an empty step input.");
            return string.Empty;
        }
    }
}

/// <summary>
/// Runs <see cref="ApprovalExpirySweeper"/> on a timer. Modelled on
/// <c>DataRetentionWorker</c>: one sweep at startup (so a backlog accumulated while the
/// process was down drains immediately) then one per tick, each in its own scope, with
/// every failure logged and swallowed so a transient database problem cannot kill the
/// worker for the lifetime of the process.
/// </summary>
public sealed class ApprovalExpiryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ApprovalExpiryOptions _options;
    private readonly ILogger<ApprovalExpiryWorker> _logger;

    public ApprovalExpiryWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<ApprovalExpiryOptions> options,
        ILogger<ApprovalExpiryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "Approval expiry sweeper is disabled; unanswered approvals will stay Pending "
                + "and their agent runs will stay in AwaitingApproval. The approve endpoint "
                + "still refuses an expired approval.");
            return;
        }

        await SweepAsync(stoppingToken);
        using var timer = new PeriodicTimer(_options.SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await SweepAsync(stoppingToken);
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sweeper = scope.ServiceProvider.GetRequiredService<ApprovalExpirySweeper>();
            await sweeper.SweepAsync(DateTime.UtcNow, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not a failure.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Approval expiry sweep failed; retrying on the next tick.");
        }
    }
}

/// <summary>Single-call registration for the approval deadline (options + sweeper + worker).</summary>
public static class ApprovalExpiryServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="ApprovalExpiryOptions"/> from the "ApprovalExpiry" section and
    /// registers the sweeper and its hosted worker.
    ///
    /// <para>Safe to omit in a test host: the deadline itself is enforced by the entity
    /// default and the approval handlers, so leaving this uncalled only means nothing
    /// sweeps.</para>
    /// </summary>
    public static IServiceCollection AddApprovalExpiry(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ApprovalExpiryOptions>(configuration.GetSection(ApprovalExpiryOptions.SectionName));
        services.AddScoped<ApprovalExpirySweeper>();
        services.AddHostedService<ApprovalExpiryWorker>();
        return services;
    }
}
