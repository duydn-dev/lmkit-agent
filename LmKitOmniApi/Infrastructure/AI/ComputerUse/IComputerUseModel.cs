namespace LmKitOmniApi.Infrastructure.AI.ComputerUse;

/// <summary>
/// Everything the model needs to choose the next action: the task goal, the system
/// prompt (which carries the action schema + the hard refusal rules), the CURRENT
/// observation (url / title / numbered interactive elements), a short history of prior
/// actions+outcomes, and the on-disk path of the current screenshot for a vision model
/// to look at (null when none was captured).
/// </summary>
public sealed record ComputerUsePrompt(
    string TaskGoal,
    string SystemPrompt,
    ComputerUseObservation Observation,
    IReadOnlyList<string> History,
    string? ScreenshotPath);

/// <summary>
/// The one model call the loop makes each step, behind a seam so the loop is testable
/// with a scripted fake (no model load, no inference). The default implementation
/// (<see cref="ComputerUseModel"/>) drives the vision model via <c>LmModelManager</c>
/// and is LIVE-ONLY. Returns the model's RAW text — the loop parses it with
/// <see cref="ComputerUseActionParser"/>, so the model may wrap the JSON in prose/fences.
/// </summary>
public interface IComputerUseModel
{
    Task<string> DecideNextActionAsync(ComputerUsePrompt prompt, CancellationToken ct = default);
}
