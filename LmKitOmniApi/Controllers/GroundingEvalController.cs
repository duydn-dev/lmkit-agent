using LmKitOmniApi.Infrastructure.AI.ComputerUse.Eval;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// GROUNDING EVALUATION harness endpoint: measures how well the computer-use model grounds
/// its next action (does it pick a valid, correct element <c>ref</c>?) so grounding quality
/// is a NUMBER (<c>GroundingAccuracy</c> / <c>HallucinationRate</c>) tuning can target.
///
/// A single <c>POST</c> takes either a body of eval cases or (empty/omitted body) a small
/// built-in default fixture set, runs the model against each, and returns the aggregate
/// report. It never touches a browser, executes an action, or navigates — it only reads what
/// the model would DECIDE, so it is a pure diagnostic.
///
/// <b>Admin-only</b> and <b>OFF BY DEFAULT</b>: when the harness is disabled (the default),
/// this returns <c>501 Not Implemented</c> and nothing runs. Wiring is documented in
/// <c>GROUNDING-EVAL-INTEGRATION.md</c> (no <c>Program.cs</c>/<c>appsettings.json</c> edits
/// are shipped here).
/// </summary>
[ApiController]
[Route("api/computer-use/grounding-eval")]
[Authorize(Roles = "Admin")]
public sealed class GroundingEvalController : ApiControllerBase
{
    /// <summary>Upper bound on cases per request — a diagnostic that drives the model repeatedly.</summary>
    private const int MaxCases = 200;
    private const int MaxTaskChars = 4000;

    private readonly IGroundingEvaluator _evaluator;
    private readonly ILogger<GroundingEvalController> _logger;

    public GroundingEvalController(IGroundingEvaluator evaluator, ILogger<GroundingEvalController> logger)
    {
        _evaluator = evaluator;
        _logger = logger;
    }

    /// <summary>The request body: a list of cases. Null/empty ⇒ the built-in default fixture set.</summary>
    public sealed record RunRequest(IReadOnlyList<GroundingEvalCase>? Cases);

    /// <summary>
    /// Runs the grounding evaluation and returns the report. <c>501</c> when the harness is
    /// disabled (the default); <c>400</c> when the supplied cases are malformed or exceed the
    /// per-request cap.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting("ai-agent")]
    public async Task<IActionResult> Run(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RunRequest? request,
        CancellationToken ct)
    {
        // OFF BY DEFAULT: refuse cleanly (501) before touching the model.
        if (!_evaluator.IsEnabled)
            return StatusCode(StatusCodes.Status501NotImplemented, "Grounding-eval harness is not enabled on this server.");

        // Body supplied → validate + use it; body empty/omitted → the built-in default fixtures.
        IReadOnlyList<GroundingEvalCase> cases;
        if (request?.Cases is { Count: > 0 } supplied)
        {
            if (supplied.Count > MaxCases)
                return BadRequest($"At most {MaxCases} cases per request (got {supplied.Count}).");

            for (var i = 0; i < supplied.Count; i++)
            {
                var c = supplied[i];
                if (c is null || c.Observation is null)
                    return BadRequest($"Case #{i} is missing its observation.");
                if (string.IsNullOrWhiteSpace(c.TaskGoal) || c.TaskGoal.Length > MaxTaskChars)
                    return BadRequest($"Case #{i} needs a non-empty taskGoal of at most {MaxTaskChars} characters.");
            }

            cases = supplied;
        }
        else
        {
            cases = GroundingEvalFixtures.Default();
        }

        try
        {
            var report = await _evaluator.EvaluateAsync(cases, ct);
            return Ok(report);
        }
        catch (InvalidOperationException ex)
        {
            // Backstop: the evaluator also refuses when disabled (race between the check above
            // and a config flip). Surface it as 501, consistent with the disabled path.
            _logger.LogWarning(ex, "Grounding-eval refused: {Message}", ex.Message);
            return StatusCode(StatusCodes.Status501NotImplemented, "Grounding-eval harness is not enabled on this server.");
        }
    }
}
