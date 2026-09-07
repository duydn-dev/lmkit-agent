namespace LmKitOmniApi.Application.AgentRuns;

/// <summary>
/// The complete <see cref="Domain.Entities.AgentRun.ResumeState"/> vocabulary — the
/// same "one source of truth for a spelling" role <see cref="AgentRunStatuses"/> plays
/// for the run lifecycle, and for the same reason: the reconciler that writes
/// <see cref="Pending"/>, the claim predicate that consumes it and the drain that
/// cleans it up must never disagree.
///
/// <para>This is a SEPARATE axis from <see cref="AgentRunStatuses"/> on purpose. The
/// status answers "what should a human be told this run is doing"; the resume state
/// answers "does the continuation machinery still owe this run a pass, and has someone
/// claimed it". Folding the two together would have meant inventing new status words
/// that no existing reader (the agent-run page's status pills, the run list) knows —
/// a queued run genuinely IS running again, so it says <see cref="AgentRunStatuses.Running"/>
/// and keeps its bookkeeping here.</para>
/// </summary>
public static class AgentRunResumeStates
{
    /// <summary>
    /// The run is owed a continuation pass and nobody is driving it. Written by
    /// <c>AgentRunApprovalReconciler</c> when a human approves the gated call, and
    /// written back by a worker that was interrupted mid-pass.
    /// </summary>
    public const string Pending = "Pending";

    /// <summary>
    /// A worker has claimed the continuation and holds it until
    /// <see cref="Domain.Entities.AgentRun.ResumeLeaseUntilUtc"/>. The claim is a
    /// conditional UPDATE, so exactly one worker — on one replica — can ever be here for
    /// a given run at a given time.
    /// </summary>
    public const string Claimed = "Claimed";
}
