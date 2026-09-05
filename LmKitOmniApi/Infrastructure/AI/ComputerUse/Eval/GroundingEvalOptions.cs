namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Eval;

/// <summary>
/// Configuration for the GROUNDING EVALUATION harness — an offline measurement tool that
/// scores how well the plugged-in <see cref="IComputerUseModel"/> GROUNDS its next action:
/// given a fixed observation (url / title / numbered interactive elements) and a task goal,
/// does it pick a valid, correct element <c>ref</c> (vs. hallucinating a ref that isn't on
/// the page, picking a real-but-wrong element, emitting a non-element action, or producing
/// malformed output)? It turns "grounding quality" into numbers (<c>GroundingAccuracy</c> /
/// <c>HallucinationRate</c>) so model/prompt/grammar tuning has a target.
///
/// DISABLED BY DEFAULT. It is a diagnostic surface that drives the model repeatedly, so it
/// only runs when an operator explicitly enables it. When disabled,
/// <see cref="IGroundingEvaluator.IsEnabled"/> reports false, the evaluator refuses
/// (throws) if called, and the controller returns 501. Bound from the "GroundingEval"
/// configuration section.
/// </summary>
public sealed class GroundingEvalOptions
{
    public const string SectionName = "GroundingEval";

    /// <summary>Master switch. False (default) = the grounding-eval harness is off everywhere.</summary>
    public bool Enabled { get; set; } = false;
}
