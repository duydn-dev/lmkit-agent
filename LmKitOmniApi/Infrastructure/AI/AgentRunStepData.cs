namespace LmKitOmniApi.Infrastructure.AI;

/// <summary>
/// One tool step captured during an agent run: the action chosen, the input
/// passed, and the (untrusted) observation returned. Captured at the
/// orchestrator's single tool seam and handed to the run handler for persistence
/// as an <c>AgentRunStep</c>; also mirrored to the client as a <c>[STEP:]</c>
/// stream marker. Only populated when the caller supplies a sink (agent runs);
/// ordinary chat passes none, so its behavior is unchanged.
/// </summary>
public sealed record AgentRunStepData(string Action, string Input, string Observation);
