using LmKitOmniApi.Domain.Entities;

namespace LmKitOmniApi.Application.Approvals;

/// <summary>
/// Configuration for the human-in-the-loop approval deadline, bound from the
/// "ApprovalExpiry" configuration section.
///
/// <para><b>What this bounds.</b> A <see cref="TaskApproval"/> used to have no deadline
/// at all: an unanswered row stayed <c>Pending</c> forever, the <see cref="Domain.Entities.AgentRun"/>
/// parked on it stayed at <c>AwaitingApproval</c> with a null <c>CompletedAtUtc</c> forever,
/// the pending list grew without bound — and, the part that actually matters, the approve
/// endpoint would still execute a side-effecting tool call proposed weeks earlier against a
/// payload captured then. These options put a clock on that.</para>
///
/// <para><b>Distinct from computer-use.</b> <c>ComputerUseOptions.ApprovalTimeoutSeconds</c>
/// (90s) is a different mechanism: an IN-PROCESS wait, where the requesting loop blocks on
/// a decision and fails closed when it does not arrive. That timeout abandons the wait but
/// never resolves the row. These options are the DURABLE deadline stamped on the row itself,
/// enforced by the API and by a background sweeper long after the requesting process is
/// gone. The two do not interact — 24 hours is orders of magnitude beyond 90 seconds, so a
/// computer-use gate has always failed closed long before its row can expire.</para>
/// </summary>
public sealed class ApprovalExpiryOptions
{
    public const string SectionName = "ApprovalExpiry";

    /// <summary>
    /// Whether the background sweeper runs. Default true — the deadline is a safety
    /// property, not an opt-in feature.
    ///
    /// <para>Turning this off does NOT make a stale approval executable: the deadline is
    /// stamped on the row at creation and is enforced independently by
    /// <c>ApproveTaskCommandHandler</c> and <c>GetPendingApprovalsQueryHandler</c>. All
    /// that is lost is the automatic closing of parked runs and the terminal status on the
    /// row.</para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long a new approval stays answerable. Defaults to
    /// <see cref="TaskApproval.DefaultTimeToLiveHours"/> so this option and the entity's
    /// own fallback can never disagree. Values below 1 are clamped by
    /// <see cref="TimeToLive"/>.
    /// </summary>
    public int TimeToLiveHours { get; set; } = TaskApproval.DefaultTimeToLiveHours;

    /// <summary>
    /// Seconds between sweeps. Default 300 (5 minutes): the deadline is measured in hours,
    /// so sweeping harder buys nothing, and a parked run being closed up to five minutes
    /// late is invisible next to a 24-hour wait.
    /// </summary>
    public int SweepIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Rows read per batch. The sweeper pages through overdue approvals rather than
    /// materializing the whole backlog, so a tenant that accumulated a million unanswered
    /// approvals cannot exhaust the API's memory on the first tick after a deploy.
    /// </summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>
    /// Batches per sweep. Bounds one pass so a huge backlog is drained over several ticks
    /// instead of monopolizing a connection for an unbounded time; the remainder is picked
    /// up on the next tick. Default 25 ⇒ up to 5,000 approvals closed per sweep.
    /// </summary>
    public int MaxBatchesPerSweep { get; set; } = 25;

    /// <summary>Validated lifetime for a newly created approval.</summary>
    public TimeSpan TimeToLive => TimeSpan.FromHours(Math.Max(1, TimeToLiveHours));

    /// <summary>Validated sweep interval.</summary>
    public TimeSpan SweepInterval => TimeSpan.FromSeconds(Math.Clamp(SweepIntervalSeconds, 5, 86_400));

    /// <summary>Validated batch size.</summary>
    public int EffectiveBatchSize => Math.Clamp(BatchSize, 1, 5_000);

    /// <summary>Validated batch cap per sweep.</summary>
    public int EffectiveMaxBatchesPerSweep => Math.Clamp(MaxBatchesPerSweep, 1, 1_000);
}
