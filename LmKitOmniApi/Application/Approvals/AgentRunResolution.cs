namespace LmKitOmniApi.Application.Approvals;

/// <summary>
/// What resolving a <see cref="Domain.Entities.TaskApproval"/> did to the
/// <see cref="Domain.Entities.AgentRun"/> parked on it.
///
/// <para>Replaces the earlier <c>bool</c> because there are now three outcomes, not two,
/// and the caller has to be able to tell the new one apart: a queued continuation is the
/// only case where somebody still has to be woken up.</para>
/// </summary>
public enum AgentRunResolution
{
    /// <summary>
    /// No agent run was parked on this approval — the ordinary chat case, where the
    /// client drives its own continuation and nothing here should touch anything.
    /// </summary>
    NoParkedRun = 0,

    /// <summary>
    /// A parked run reached a terminal status and will never change again. Rejections,
    /// failures and expiries always land here; so does an approval in a host with no
    /// continuation worker, or on a run that has spent its resume budget.
    /// </summary>
    Terminal = 1,

    /// <summary>
    /// A parked run was handed back to the ReAct loop: it is <c>Running</c> again with
    /// <c>ResumeState = Pending</c>, and <c>AgentRunResumeService</c> will drive the
    /// continuation. The caller should nudge the worker so it does not wait out a poll.
    /// </summary>
    QueuedForResume = 2
}
