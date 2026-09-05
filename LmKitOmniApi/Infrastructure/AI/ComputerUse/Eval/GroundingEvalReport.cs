using System.Text.Json.Serialization;

namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Eval;

/// <summary>
/// The five mutually-exclusive outcomes of one grounding-eval case, ordered from worst to
/// best grounding. Classification keys off the parsed action's <c>ref</c>: an action that
/// carries no <c>ref</c> targets no element (<see cref="NonElement"/>), one whose <c>ref</c>
/// isn't on the page is a hallucination (<see cref="Hallucinated"/>), and a real ref is
/// either the wrong element (<see cref="ValidButWrong"/>) or an accepted one
/// (<see cref="Correct"/>). Serialized by NAME in reports for readability.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GroundingOutcome
{
    /// <summary>The model's output could not be parsed into a valid action at all.</summary>
    Malformed,

    /// <summary>Parsed, but the action targets no element ref (done / ask / scroll / navigate /
    /// wait / screenshot / key, or a coordinate-only click/type).</summary>
    NonElement,

    /// <summary>A ref-targeting action whose <c>ref</c> is NOT present in the observation's
    /// element list — a hallucinated (or stale) element.</summary>
    Hallucinated,

    /// <summary>The chosen <c>ref</c> IS a real element in the observation, but not one of the
    /// case's acceptable refs — a valid element, wrong choice.</summary>
    ValidButWrong,

    /// <summary>The chosen <c>ref</c> is one of the case's acceptable refs — a correct pick.</summary>
    Correct,
}

/// <summary>
/// Per-case detail of what the model did on one grounding-eval case: the raw model output,
/// the classified <see cref="Outcome"/>, the ref it chose (null when the action carried
/// none), the case's expectations, and — for a failed parse — the parser's reason. Kept
/// alongside the aggregate counts so a run is auditable case-by-case, not just as a score.
/// </summary>
public sealed record GroundingEvalCaseResult
{
    public required string TaskGoal { get; init; }
    public required GroundingOutcome Outcome { get; init; }

    /// <summary>The element ref the model targeted, or null for a non-element/malformed action.</summary>
    public int? ChosenRef { get; init; }

    /// <summary>The parsed action verb (e.g. "Click"), or null when parsing failed.</summary>
    public string? ActionType { get; init; }

    public required int ExpectedRef { get; init; }
    public required IReadOnlyList<int> AcceptableRefs { get; init; }

    /// <summary>The model's raw text output for this case (truncated for the report payload).</summary>
    public string? RawOutput { get; init; }

    /// <summary>The parser's rejection reason when <see cref="Outcome"/> is
    /// <see cref="GroundingOutcome.Malformed"/>; otherwise null.</summary>
    public string? ParseError { get; init; }
}

/// <summary>
/// The aggregate result of a grounding-eval run: a count per <see cref="GroundingOutcome"/>
/// bucket, the <see cref="Total"/>, the two headline ratios, and the full per-case detail
/// list. <see cref="GroundingAccuracy"/> is the fraction of cases the model got exactly
/// right (Correct / Total); <see cref="HallucinationRate"/> is the fraction where it picked
/// a ref that wasn't on the page (Hallucinated / Total) — the metric grounding tuning most
/// wants to drive to zero. Both are 0 for an empty run.
/// </summary>
public sealed record GroundingEvalReport
{
    public int Malformed { get; init; }
    public int NonElement { get; init; }
    public int Hallucinated { get; init; }
    public int ValidButWrong { get; init; }
    public int Correct { get; init; }
    public int Total { get; init; }

    /// <summary>Correct / Total — the fraction of cases grounded to an acceptable element. 0 when empty.</summary>
    public double GroundingAccuracy => Total == 0 ? 0.0 : (double)Correct / Total;

    /// <summary>Hallucinated / Total — the fraction of cases targeting a ref not on the page. 0 when empty.</summary>
    public double HallucinationRate => Total == 0 ? 0.0 : (double)Hallucinated / Total;

    public IReadOnlyList<GroundingEvalCaseResult> Cases { get; init; } = Array.Empty<GroundingEvalCaseResult>();
}
