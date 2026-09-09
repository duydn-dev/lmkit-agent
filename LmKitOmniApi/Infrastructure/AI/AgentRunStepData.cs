namespace LmKitOmniApi.Infrastructure.AI;

/// <summary>
/// One tool step captured during an agent run: the action chosen, the input
/// passed, and the (untrusted) observation returned. Captured at the
/// orchestrator's single tool seam and handed to the run handler for persistence
/// as an <c>AgentRunStep</c>; also mirrored to the client as a <c>[STEP:]</c>
/// stream marker. Only populated when the caller supplies a sink (agent runs);
/// ordinary chat passes none, so its behavior is unchanged.
/// </summary>
public sealed record AgentRunStepData(string Action, string Input, string Observation)
{
    /// <summary>
    /// <see cref="Action"/> of the step the orchestrator records when the inference admission
    /// queue REFUSES a run (wait bound exceeded, or queue full). It is written for the run's
    /// timeline, so the agent-run page can say why the run stopped, and it is the one step
    /// <c>AgentRunResumeService</c> drops before requeueing: a refusal is capacity, not prior
    /// progress, and replaying it into the next attempt's prompt would read as something the
    /// agent did.
    /// </summary>
    public const string AdmissionRefusedAction = "admission_refused";
}
