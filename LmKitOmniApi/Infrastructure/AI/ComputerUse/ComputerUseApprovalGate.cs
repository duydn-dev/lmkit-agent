using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.ComputerUse;

/// <summary>
/// Default <see cref="IComputerUseApprovalGate"/> that reuses the existing approval
/// substrate. For each side-effecting action it:
///  1. ensures a hidden computer-use <see cref="ChatSession"/> exists (the HITL substrate,
///     excluded from the chat list exactly like an agent run) so the approval row's FK is
///     valid, then
///  2. inserts a <see cref="TaskApproval"/> (status Pending, owner-scoped, payload
///     encrypted by <see cref="TaskApprovalPayloadProtector"/>) — which surfaces in the
///     user's existing pending-approvals list, and
///  3. waits, polling the row, for a human decision.
///
/// It FAILS CLOSED: it returns true ONLY when the row reaches an explicit approved state
/// (<c>Approved</c>/<c>Completed</c>); a rejected/failed state, the timeout
/// (<see cref="ComputerUseOptions.ApprovalTimeoutSeconds"/>), cancellation, or any error
/// all return false so the action is NOT executed.
///
/// <para><b>The gate resolves its own row.</b> Failing closed used to mean walking away:
/// the ~90-second wait elapsed, the loop refused the action and moved on, and the
/// <c>Pending</c> row stayed in the user's approval list until the 24-hour expiry sweeper
/// collected it. That is garbage collection, not a resolution — for a day the list offered
/// a decision on an action no longer reachable by any decision, since the loop that was
/// waiting for it is gone. Every exit path now CLAIMS its own row
/// (<see cref="ResolveIfStillPendingAsync"/>): the wait elapsing writes
/// <see cref="TimedOutStatus"/>, the run being cancelled mid-wait writes
/// <see cref="AbandonedStatus"/>. The row leaves the pending list within the wait, and the
/// approve/reject handlers — which all claim on <c>Status = 'Pending'</c> — answer
/// Conflict rather than acting on something nothing is listening to.</para>
///
/// <para>The claim is conditional, so a human decision landing in the same instant always
/// wins: the update matches nothing and the gate reads back and honours what beat it. It
/// deliberately runs on <see cref="CancellationToken.None"/> — the cancellation path is
/// exactly where the row most needs closing.</para>
///
/// <para><b>No race with the expiry sweeper.</b> The row's <c>ExpiresAtUtc</c> is left at
/// the entity default and <c>ApprovalExpiryOptions.TimeToLive</c> is clamped to a 1-hour
/// minimum, both orders of magnitude beyond this wait, so the gate always claims first and
/// the sweeper then finds a non-Pending row. If an operator ever configured
/// <see cref="ComputerUseOptions.ApprovalTimeoutSeconds"/> past that hour the sweeper
/// could win instead — which is why <see cref="TaskApproval.ExpiredStatus"/> is in the
/// denied set below: the gate then exits promptly and still fails closed.</para>
///
/// LIVE-ONLY: the waiting + database interaction are exercised in the running stack, not
/// in CI (the loop's own tests inject a scripted approver through the
/// <see cref="IComputerUseApprovalGate"/> seam). Computer-use approvals are resolved
/// through the dedicated endpoints on <c>ComputerUseController</c>
/// (<c>/api/agent/computer-use/approvals/{id}/approve|reject</c>), which set the status
/// this gate waits on WITHOUT routing the action through the generic tool dispatcher
/// (the action executes inside the loop, not via a dispatcher tool). The generic
/// <c>/api/taskapproval/{id}/approve</c> now records the same decision instead of trying
/// to dispatch <c>COMPUTER_USE</c> as if it were a tool.
/// </summary>
public sealed class ComputerUseApprovalGate : IComputerUseApprovalGate
{
    /// <summary>
    /// Terminal status written when the wait elapses with no human decision. Deliberately
    /// the SAME word the expiry sweeper uses for an unanswered approval: both mean
    /// "nobody answered in time", and every reader already behaves correctly for it — the
    /// pending query hides it, the approve handler's claim refuses it, and
    /// <c>AgentRunStatuses.Expired</c> is the matching run-side vocabulary.
    /// </summary>
    public const string TimedOutStatus = TaskApproval.ExpiredStatus;

    /// <summary>Terminal status written when the RUN that raised the request goes away mid-wait.</summary>
    public const string AbandonedStatus = TaskApproval.CancelledStatus;

    /// <summary>Stored on a timed-out row so the user can see why it closed itself.</summary>
    public const string TimedOutComment =
        "Hết thời gian chờ phê duyệt — hành động computer-use đã bị từ chối an toàn và không được thực thi.";

    /// <summary>Stored when the computer-use session ended before anyone decided.</summary>
    public const string AbandonedComment =
        "Phiên computer-use đã kết thúc trước khi có quyết định — yêu cầu phê duyệt được đóng lại.";

    // Terminal statuses the gate recognises. "Approved" is the go-ahead the dedicated
    // resolve endpoint sets; "Completed" is accepted for forward-compatibility.
    private static readonly HashSet<string> ApprovedStatuses =
        new(new[] { "Approved", "Completed" }, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DeniedStatuses =
        new(new[] { "Rejected", "Failed", "Cancelled", "Canceled", "Denied", TaskApproval.ExpiredStatus },
            StringComparer.OrdinalIgnoreCase);

    /// <summary>How often the row is re-read while waiting, at most.</summary>
    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Floor, so a tiny configured budget cannot turn the wait into a hot loop.</summary>
    private static readonly TimeSpan MinPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Poll interval for a given approval budget: a quarter of it, bounded by
    /// <see cref="MinPollInterval"/> and <see cref="MaxPollInterval"/>.
    ///
    /// <para>The interval used to be a flat 2 seconds, which meant the gate could not
    /// honour a budget shorter than its own sleep — with
    /// <c>ApprovalTimeoutSeconds = 1</c> it slept two seconds and checked once, i.e.
    /// overshot its deadline by 100%. Taking a quarter keeps the overshoot below 25% for
    /// any configuration. At the SHIPPED 90-second budget a quarter is 22.5s, so the cap
    /// applies and the interval stays exactly 2 seconds — this changes nothing that runs
    /// in production, and <c>ThePollIntervalIsUnchangedAtTheShippedBudget</c> pins that.</para>
    /// </summary>
    internal static TimeSpan PollIntervalFor(int approvalTimeoutSeconds)
    {
        var quarter = TimeSpan.FromSeconds(Math.Max(1, approvalTimeoutSeconds)) / 4;
        if (quarter > MaxPollInterval) return MaxPollInterval;
        return quarter < MinPollInterval ? MinPollInterval : quarter;
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TaskApprovalPayloadProtector _payloadProtector;
    private readonly ComputerUseOptions _options;
    private readonly ILogger<ComputerUseApprovalGate> _logger;

    public ComputerUseApprovalGate(
        IServiceScopeFactory scopeFactory,
        TaskApprovalPayloadProtector payloadProtector,
        IOptions<ComputerUseOptions> options,
        ILogger<ComputerUseApprovalGate> logger)
    {
        _scopeFactory = scopeFactory;
        _payloadProtector = payloadProtector;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> RequestAsync(ComputerUseApprovalRequest request, CancellationToken ct = default)
    {
        try
        {
            await CreatePendingApprovalAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Could not even record the request — fail closed.
            _logger.LogError(ex, "❌ [ComputerUse] Không tạo được yêu cầu phê duyệt {ApprovalId}.", request.ApprovalId);
            return false;
        }

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, _options.ApprovalTimeoutSeconds));
        var pollInterval = PollIntervalFor(_options.ApprovalTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(pollInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The run went away → not approved. Close the row first so it cannot
                // outlive the loop that raised it. A decision that had already landed is
                // left exactly as the human wrote it (the claim is conditional on
                // Pending); it simply no longer has anywhere to be delivered, and
                // returning true here would execute an action for a session that is gone.
                _logger.LogInformation(
                    "🛑 [ComputerUse] Phiên kết thúc khi đang chờ phê duyệt {ApprovalId} — đóng yêu cầu.",
                    request.ApprovalId);
                await ResolveIfStillPendingAsync(request, AbandonedStatus, AbandonedComment);
                return false;
            }

            var status = await ReadStatusAsync(request, ct);
            if (status is null) continue;
            if (ApprovedStatuses.Contains(status))
            {
                _logger.LogInformation("✅ [ComputerUse] Hành động được phê duyệt (approval {ApprovalId}).", request.ApprovalId);
                return true;
            }
            if (DeniedStatuses.Contains(status))
            {
                _logger.LogInformation("🚫 [ComputerUse] Hành động bị từ chối (approval {ApprovalId}).", request.ApprovalId);
                return false;
            }
        }

        // The wait elapsed. RESOLVE the row instead of abandoning it: a Pending row nobody
        // is waiting on is an offer the product cannot honour, and it would sit in the
        // user's list until the 24-hour sweeper collected it.
        var raced = await ResolveIfStillPendingAsync(request, TimedOutStatus, TimedOutComment);
        if (raced is not null && ApprovedStatuses.Contains(raced))
        {
            // A human answered in the same instant the deadline passed. Their decision was
            // written first and the claim above matched nothing, so it stands.
            _logger.LogInformation(
                "✅ [ComputerUse] Phê duyệt {ApprovalId} đến ngay trước hạn chót — chấp nhận.", request.ApprovalId);
            return true;
        }

        _logger.LogWarning("⏱️ [ComputerUse] Phê duyệt {ApprovalId} hết thời gian chờ — từ chối an toàn.", request.ApprovalId);
        return false;
    }

    /// <summary>
    /// Atomically moves this gate's own row from <c>Pending</c> to
    /// <paramref name="terminalStatus"/>.
    ///
    /// <para>Returns <c>null</c> when the claim WON (the row was still Pending and is now
    /// closed), and otherwise the status that was already there — so a decision a human
    /// made in the same instant is reported back and honoured rather than overwritten. A
    /// database failure also returns <c>null</c>: every caller is already on a fail-closed
    /// path and must not be turned into a throw by bookkeeping; the row is then left for
    /// the expiry sweeper, and the failure is logged.</para>
    ///
    /// <para>Scoped exactly like the dedicated resolve endpoint — same tenant, same user,
    /// <c>ActionName = COMPUTER_USE</c> — so it can never close some other tool's
    /// approval. Runs on <see cref="CancellationToken.None"/> because the cancellation path
    /// is one of its callers.</para>
    ///
    /// <para>Internal rather than private so the claim contract is testable
    /// DETERMINISTICALLY: proving it through the polling loop means racing a background
    /// writer against a wall-clock deadline, which on a loaded machine decides the
    /// assertion instead of the code under test.</para>
    /// </summary>
    internal async Task<string?> ResolveIfStillPendingAsync(
        ComputerUseApprovalRequest request, string terminalStatus, string comment)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();

            var claimed = await db.TaskApprovals
                .Where(t => t.Id == request.ApprovalId
                    && t.TenantId == request.TenantId
                    && t.UserId == request.UserId
                    && t.ActionName == TaskApproval.ComputerUseActionName
                    && t.Status == "Pending")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.Status, terminalStatus)
                    .SetProperty(t => t.RejectionComment, comment)
                    .SetProperty(t => t.ResolvedAtUtc, DateTime.UtcNow),
                    CancellationToken.None);

            if (claimed > 0) return null;

            return await db.TaskApprovals
                .AsNoTracking()
                .Where(t => t.Id == request.ApprovalId
                    && t.TenantId == request.TenantId
                    && t.UserId == request.UserId)
                .Select(t => t.Status)
                .FirstOrDefaultAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "❌ [ComputerUse] Không đóng được yêu cầu phê duyệt {ApprovalId}; hàng chờ có thể còn sót lại.",
                request.ApprovalId);
            return null;
        }
    }

    private async Task CreatePendingApprovalAsync(ComputerUseApprovalRequest request, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();

        // Ensure the hidden computer-use session exists so the approval FK is valid.
        var sessionExists = await db.ChatSessions
            .AsNoTracking()
            .AnyAsync(s => s.Id == request.SessionId, ct);
        if (!sessionExists)
        {
            db.ChatSessions.Add(new ChatSession
            {
                Id = request.SessionId,
                TenantId = request.TenantId,
                UserId = request.UserId,
                Title = "Computer-use",
                IsAgentRun = true, // hidden substrate — never shows in the chat list
            });
        }

        db.TaskApprovals.Add(new TaskApproval
        {
            Id = request.ApprovalId,
            TenantId = request.TenantId,
            UserId = request.UserId,
            ChatSessionId = request.SessionId,
            ActionName = TaskApproval.ComputerUseActionName,
            // ExpiresAtUtc is deliberately LEFT at the entity default (24h, and never
            // below the 1-hour clamp on ApprovalExpiryOptions.TimeToLive). Shortening it
            // to this gate's ~90-second wait would let the expiry sweeper claim the row
            // while the gate is still polling it — the two mechanisms must not race, and
            // the gate resolving its own row is what keeps the row short-lived.
            ParametersJson = _payloadProtector.Protect(request.Details),
            Status = "Pending",
        });

        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "⚠️ [ComputerUse] Chờ phê duyệt hành động '{Summary}' (approval {ApprovalId}).",
            request.ActionSummary, request.ApprovalId);
    }

    private async Task<string?> ReadStatusAsync(ComputerUseApprovalRequest request, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
            return await db.TaskApprovals
                .AsNoTracking()
                .Where(t => t.Id == request.ApprovalId
                    && t.TenantId == request.TenantId
                    && t.UserId == request.UserId)
                .Select(t => t.Status)
                .FirstOrDefaultAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "🔁 [ComputerUse] Lỗi khi đọc trạng thái phê duyệt; sẽ thử lại.");
            return null;
        }
    }
}
