using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Eval;

/// <summary>
/// Default <see cref="IGroundingEvaluator"/>. For each case it builds a
/// <see cref="ComputerUsePrompt"/> reusing <see cref="ComputerUseAgent.SystemPrompt"/> (so
/// the model is prompted EXACTLY as in the live loop — same schema, same rules), asks the
/// model once, parses the reply with <see cref="ComputerUseActionParser"/>, and classifies
/// the outcome with <see cref="Classify"/>. It never mutates the world — no executor, no
/// approval gate, no navigation — it only reads what the model would DECIDE, so it is safe
/// to run repeatedly for measurement.
///
/// A single case's model error is contained: it is recorded as a
/// <see cref="GroundingOutcome.Malformed"/> outcome (with the exception message as the parse
/// error) so one bad case can't abort the batch — except cancellation, which propagates.
/// </summary>
public sealed class GroundingEvaluator : IGroundingEvaluator
{
    /// <summary>Max characters of raw model output retained per case in the report payload.</summary>
    private const int MaxRawOutputChars = 600;

    private readonly IComputerUseModel _model;
    private readonly GroundingEvalOptions _options;
    private readonly ILogger<GroundingEvaluator> _logger;

    public GroundingEvaluator(
        IComputerUseModel model,
        IOptions<GroundingEvalOptions> options,
        ILogger<GroundingEvaluator> logger)
    {
        _model = model;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsEnabled => _options.Enabled;

    /// <inheritdoc />
    public async Task<GroundingEvalReport> EvaluateAsync(IReadOnlyList<GroundingEvalCase> cases, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cases);

        // REFUSE while disabled — the harness must never drive the model unless opted in.
        if (!IsEnabled)
            throw new InvalidOperationException(
                "Grounding-eval harness is disabled. Set GroundingEval:Enabled=true to run it.");

        var results = new List<GroundingEvalCaseResult>(cases.Count);
        int malformed = 0, nonElement = 0, hallucinated = 0, validButWrong = 0, correct = 0;

        foreach (var evalCase in cases)
        {
            ct.ThrowIfCancellationRequested();

            var acceptable = evalCase.EffectiveAcceptableRefs;
            string? raw = null;
            ComputerUseAction? action = null;
            var parsed = false;
            string? parseError = null;

            try
            {
                var prompt = new ComputerUsePrompt(
                    evalCase.TaskGoal,
                    ComputerUseAgent.SystemPrompt,
                    evalCase.Observation,
                    History: Array.Empty<string>(),
                    ScreenshotPath: null);

                raw = await _model.DecideNextActionAsync(prompt, ct);
                parsed = ComputerUseActionParser.TryParse(raw, out action, out parseError);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // cancellation aborts the whole run — don't swallow it as a case failure
            }
            catch (Exception ex)
            {
                // A model-level failure on one case is contained as a malformed outcome so the
                // batch still completes and the failure shows up in the per-case detail.
                _logger.LogWarning(ex, "⚠️ [GroundingEval] Lỗi khi lấy hành động từ mô hình cho một case — tính là Malformed.");
                parsed = false;
                action = null;
                parseError = $"model error: {ex.Message}";
            }

            var outcome = Classify(parsed, action, evalCase.Observation, acceptable);
            switch (outcome)
            {
                case GroundingOutcome.Malformed: malformed++; break;
                case GroundingOutcome.NonElement: nonElement++; break;
                case GroundingOutcome.Hallucinated: hallucinated++; break;
                case GroundingOutcome.ValidButWrong: validButWrong++; break;
                case GroundingOutcome.Correct: correct++; break;
            }

            results.Add(new GroundingEvalCaseResult
            {
                TaskGoal = evalCase.TaskGoal,
                Outcome = outcome,
                ChosenRef = action?.Ref,
                ActionType = action?.Type.ToString(),
                ExpectedRef = evalCase.ExpectedRef,
                AcceptableRefs = acceptable,
                RawOutput = Truncate(raw),
                ParseError = outcome == GroundingOutcome.Malformed ? parseError : null,
            });
        }

        return new GroundingEvalReport
        {
            Malformed = malformed,
            NonElement = nonElement,
            Hallucinated = hallucinated,
            ValidButWrong = validButWrong,
            Correct = correct,
            Total = cases.Count,
            Cases = results,
        };
    }

    /// <summary>
    /// Classifies one decision into a <see cref="GroundingOutcome"/>. The discriminator is the
    /// parsed action's <c>ref</c>:
    /// <list type="bullet">
    ///   <item>parse failed → <see cref="GroundingOutcome.Malformed"/>;</item>
    ///   <item>no <c>ref</c> (done/ask/scroll/navigate/wait/screenshot/key, or a coordinate-only
    ///   click/type) → <see cref="GroundingOutcome.NonElement"/>;</item>
    ///   <item>a <c>ref</c> not in the observation → <see cref="GroundingOutcome.Hallucinated"/>;</item>
    ///   <item>a real <c>ref</c> outside the acceptable set → <see cref="GroundingOutcome.ValidButWrong"/>;</item>
    ///   <item>a <c>ref</c> in the acceptable set → <see cref="GroundingOutcome.Correct"/>.</item>
    /// </list>
    /// Static and side-effect free so it is directly unit-testable.
    /// </summary>
    public static GroundingOutcome Classify(
        bool parsed, ComputerUseAction? action, ComputerUseObservation observation, IReadOnlyList<int> acceptableRefs)
    {
        if (!parsed || action is null)
            return GroundingOutcome.Malformed;

        if (action.Ref is not int chosenRef)
            return GroundingOutcome.NonElement;

        var existsInObservation = observation.Elements.Any(e => e.Ref == chosenRef);
        if (!existsInObservation)
            return GroundingOutcome.Hallucinated;

        return acceptableRefs.Contains(chosenRef)
            ? GroundingOutcome.Correct
            : GroundingOutcome.ValidButWrong;
    }

    private static string? Truncate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= MaxRawOutputChars ? s : s[..MaxRawOutputChars] + "…";
    }
}
