namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Eval;

/// <summary>
/// One grounding-evaluation FIXTURE case: a fixed <see cref="Observation"/> (the page the
/// model sees) plus a <see cref="TaskGoal"/>, and the ground-truth of which element the
/// model SHOULD target. <see cref="ExpectedRef"/> is the single canonical right answer;
/// <see cref="AcceptableRefs"/> optionally widens "correct" to a set (e.g. two equivalent
/// buttons) — when null or empty, only <see cref="ExpectedRef"/> counts as correct
/// (surfaced via <see cref="EffectiveAcceptableRefs"/>).
///
/// The harness feeds <see cref="Observation"/> + <see cref="TaskGoal"/> to the model and
/// classifies the ref it picks against these expectations; the case carries no screenshot
/// (grounding is measured against the accessibility element list, not pixels).
/// </summary>
public sealed record GroundingEvalCase(
    string TaskGoal,
    ComputerUseObservation Observation,
    int ExpectedRef,
    IReadOnlyList<int>? AcceptableRefs = null)
{
    /// <summary>
    /// The refs that count as a CORRECT pick: <see cref="AcceptableRefs"/> when it carries
    /// at least one entry, otherwise just <see cref="ExpectedRef"/>. Never empty, so a case
    /// always has a well-defined right answer.
    /// </summary>
    public IReadOnlyList<int> EffectiveAcceptableRefs =>
        AcceptableRefs is { Count: > 0 } ? AcceptableRefs : new[] { ExpectedRef };
}
