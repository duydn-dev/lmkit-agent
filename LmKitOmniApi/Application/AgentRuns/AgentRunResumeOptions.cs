namespace LmKitOmniApi.Application.AgentRuns;

/// <summary>
/// Bounds on the continuation of an agent run whose human-in-the-loop gate was
/// approved, bound from the "AgentRunResume" configuration section.
///
/// <para><b>Why every one of these is a bound and not a knob.</b> A continuation pass
/// runs OUTSIDE any HTTP request and takes the single chat inference lease
/// (<c>SemaphoreLimits:Chat</c> is 1 in this deployment), so an unbounded resume storm
/// would starve every interactive user — strictly worse than the honest stop this
/// feature replaces. Each option below closes one way that could happen: a run that
/// keeps gating and being approved (<see cref="MaxResumesPerRun"/>), a run that plans
/// forever (<see cref="MaxTotalSteps"/>), a worker that crashed holding a claim
/// (<see cref="LeaseSeconds"/>), and a backlog drained all at once
/// (<see cref="MaxRunsPerPass"/>).</para>
/// </summary>
public sealed class AgentRunResumeOptions
{
    public const string SectionName = "AgentRunResume";

    /// <summary>
    /// Whether an approved gated call continues the run. Default true — this is the fix
    /// for a run that used to stop after one approved tool call.
    ///
    /// <para>Turning it off restores exactly the previous behaviour: the approved call
    /// is still recorded as a real step and the run still reaches
    /// <c>CompletedAfterApproval</c>, which was always truthful, just smaller. It also
    /// makes the worker DRAIN anything already queued to that same terminal state on its
    /// next start, so flipping this off can never strand a run mid-continuation.</para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many continuation passes one run may be granted, across all of its approval
    /// gates. Default 3: a resumed run may gate again, and each further approval buys
    /// another pass, but a plan that needs a fourth human decision is one the user should
    /// be restating rather than one the system should keep feeding.
    ///
    /// <para>Counted on the CLAIM (see <see cref="Domain.Entities.AgentRun.ResumeCount"/>),
    /// so a crash loop burns the budget too and cannot spin forever.</para>
    /// </summary>
    public int MaxResumesPerRun { get; set; } = 3;

    /// <summary>
    /// Hard ceiling on <see cref="Domain.Entities.AgentRunStep"/> rows for one run,
    /// across the original pass and every continuation. Default 24. The orchestrator
    /// already caps a single ReAct pass at 5 iterations; this is the cap on the SUM,
    /// which is the number the original design never had because there was only ever one
    /// pass. A run at the ceiling is closed truthfully instead of resumed.
    /// </summary>
    public int MaxTotalSteps { get; set; } = 24;

    /// <summary>
    /// Seconds between polls for queued continuations. Default 5. The worker is also
    /// woken directly by the approve request on the same replica
    /// (<see cref="AgentRunResumeQueue.Notify"/>), so this interval only bounds how long
    /// a continuation queued on ANOTHER replica waits.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// How long a claim is owned before another worker may retake it. Default 900 (15
    /// minutes) — comfortably longer than a bounded ReAct + synthesis pass on a small
    /// local model, short enough that a killed process does not park a run for the rest
    /// of the day. Retaking still spends resume budget, so a pathological loop ends.
    /// </summary>
    public int LeaseSeconds { get; set; } = 900;

    /// <summary>
    /// Continuations driven per poll, one after another (never concurrently — the chat
    /// lease is single-permit, so parallel passes would only queue on it while holding
    /// extra database contexts). Default 4; the rest wait for the next poll.
    /// </summary>
    public int MaxRunsPerPass { get; set; } = 4;

    /// <summary>
    /// Characters of a single prior observation replayed into the continuation prompt.
    /// Default 2000. Tool output is untrusted and can be enormous; the prompt has to stay
    /// bounded or a resume would blow the context window instead of continuing.
    /// </summary>
    public int MaxObservationChars { get; set; } = 2_000;

    /// <summary>
    /// Characters of replayed prior progress in total. Default 8000. When the history
    /// does not fit, the OLDEST steps are dropped and the prompt says so — the approved
    /// observation the run is resuming ON is the last step, so it is the one that must
    /// never be the one dropped.
    /// </summary>
    public int MaxProgressChars { get; set; } = 8_000;

    /// <summary>Validated resume budget per run.</summary>
    public int EffectiveMaxResumesPerRun => Math.Clamp(MaxResumesPerRun, 0, 20);

    /// <summary>Validated total-step ceiling. Never below what one ReAct pass can already produce.</summary>
    public int EffectiveMaxTotalSteps => Math.Clamp(MaxTotalSteps, 1, 500);

    /// <summary>Validated poll interval.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Clamp(PollIntervalSeconds, 1, 3_600));

    /// <summary>Validated claim lease.</summary>
    public TimeSpan Lease => TimeSpan.FromSeconds(Math.Clamp(LeaseSeconds, 30, 86_400));

    /// <summary>Validated per-poll cap.</summary>
    public int EffectiveMaxRunsPerPass => Math.Clamp(MaxRunsPerPass, 1, 100);

    /// <summary>Validated per-observation cap.</summary>
    public int EffectiveMaxObservationChars => Math.Clamp(MaxObservationChars, 100, 100_000);

    /// <summary>Validated total-progress cap.</summary>
    public int EffectiveMaxProgressChars => Math.Clamp(MaxProgressChars, 200, 500_000);
}
