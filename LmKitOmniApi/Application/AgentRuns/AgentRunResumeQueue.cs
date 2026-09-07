using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Application.AgentRuns;

/// <summary>
/// The presence of this singleton is what tells the approve path that a continuation
/// worker exists in this process, and its <see cref="Notify"/> is how that path wakes
/// the worker without waiting out a poll interval.
///
/// <para><b>Why "presence" is the signal.</b> <c>ApproveTaskCommandHandler</c> takes this
/// as an OPTIONAL constructor dependency defaulting to <c>null</c>. It is registered only
/// by <see cref="AgentRunResumeServiceCollectionExtensions.AddAgentRunResume"/>, which
/// also registers the worker — so the two cannot come apart. A host that never calls that
/// method resolves <c>null</c> here, the reconciler is handed no resume plan, and the run
/// reaches the same truthful <c>CompletedAfterApproval</c> it always did. Queuing a
/// continuation nobody will ever drive would invent a new way for a run to hang forever,
/// which is precisely the class of bug this whole change exists to remove; deriving the
/// decision from configuration alone could not rule it out, because configuration is
/// readable whether or not the worker was wired up.</para>
///
/// <para>The wake-up is a best-effort optimisation, never a correctness requirement: the
/// queue is the <c>agent_runs.ResumeState</c> column, so a continuation queued on another
/// replica (or missed because this process was busy) is still picked up by the next poll.
/// That is why this holds a reset event rather than the work itself.</para>
/// </summary>
public sealed class AgentRunResumeQueue : IDisposable
{
    // Manual-reset semantics via a 0/1 semaphore: several approvals landing between two
    // polls collapse into one wake-up, which is all the worker needs — it drains every
    // claimable run per pass, not one per signal.
    private readonly SemaphoreSlim _signal = new(0, 1);

    public AgentRunResumeQueue(IOptions<AgentRunResumeOptions> options) => Options = options.Value;

    /// <summary>The bounds every continuation runs under.</summary>
    public AgentRunResumeOptions Options { get; }

    /// <summary>
    /// Wakes the worker now. Safe to call from any thread and any number of times;
    /// signalling an already-signalled queue is a no-op rather than an error.
    /// </summary>
    public void Notify()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* already pending — one wake-up is enough */ }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    /// <summary>
    /// Waits for a wake-up or for <paramref name="pollInterval"/> to elapse, whichever
    /// comes first. Returns without throwing when the wait is cancelled, so the worker's
    /// loop can decide what a shutdown means instead of unwinding through an exception on
    /// every stop.
    /// </summary>
    public async Task WaitAsync(TimeSpan pollInterval, CancellationToken ct)
    {
        try { await _signal.WaitAsync(pollInterval, ct); }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (ObjectDisposedException) { /* shutdown */ }
    }

    public void Dispose() => _signal.Dispose();
}
