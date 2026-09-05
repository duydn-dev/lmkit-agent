namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Eval;

/// <summary>
/// Runs the grounding-evaluation harness: for each <see cref="GroundingEvalCase"/> it asks
/// the plugged-in <see cref="IComputerUseModel"/> for the next action against the case's
/// fixed observation, then classifies the action's chosen element <c>ref</c> into one of the
/// <see cref="GroundingOutcome"/> buckets, aggregating a <see cref="GroundingEvalReport"/>
/// (counts, <c>GroundingAccuracy</c>, <c>HallucinationRate</c>, per-case detail).
///
/// OFF BY DEFAULT: <see cref="IsEnabled"/> mirrors <see cref="GroundingEvalOptions.Enabled"/>,
/// and <see cref="EvaluateAsync"/> REFUSES (throws) when disabled so the harness can never
/// drive the model unless an operator opted in.
/// </summary>
public interface IGroundingEvaluator
{
    /// <summary>True only when the harness is enabled via <see cref="GroundingEvalOptions.Enabled"/>.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Evaluates every case and returns the aggregate report. Throws
    /// <see cref="InvalidOperationException"/> when <see cref="IsEnabled"/> is false (the
    /// harness never runs while disabled) and <see cref="ArgumentNullException"/> when
    /// <paramref name="cases"/> is null. An empty case list yields an empty (all-zero) report.
    /// </summary>
    Task<GroundingEvalReport> EvaluateAsync(IReadOnlyList<GroundingEvalCase> cases, CancellationToken ct = default);
}
