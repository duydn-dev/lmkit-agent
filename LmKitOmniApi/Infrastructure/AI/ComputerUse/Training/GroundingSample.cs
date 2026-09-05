namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;

/// <summary>
/// One RAW, model-free supervised training sample captured from a vetted computer-use step.
/// Deliberately holds ONLY plain, JSON-serializable data (no <c>LMKit</c> types): the
/// recorder persists these as JSON lines with no loaded model, and ONLY the live trainer
/// port (<see cref="IGroundingAdapterTrainerPort"/>) turns a batch of these into
/// <c>LMKit.TextGeneration.Chat.ChatHistory</c> objects — which require a loaded <c>LM</c> —
/// and trains. Nothing in a CI-testable code path ever constructs a <c>ChatHistory</c>.
///
/// The supervised label is <see cref="CorrectActionJson"/>: the exact action the human
/// approved (or that executed successfully) for the page described by
/// <see cref="TaskGoal"/> + <see cref="ElementsText"/> under <see cref="SystemPrompt"/> —
/// i.e. input = what the model saw, output = the correct grounded action.
/// </summary>
public sealed record GroundingSample
{
    /// <summary>Stable id for this sample (used as the JSONL row identity).</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Owning tenant; every read/write is filtered by this.</summary>
    public Guid TenantId { get; init; }

    /// <summary>The run's task goal the model was working toward.</summary>
    public string TaskGoal { get; init; } = string.Empty;

    /// <summary>The URL of the page the model was looking at when it chose the action.</summary>
    public string PageUrl { get; init; } = string.Empty;

    /// <summary>The numbered interactive-element list text that was shown to the model.</summary>
    public string ElementsText { get; init; } = string.Empty;

    /// <summary>Owner-scoped id of the page screenshot, when one was captured (else null).</summary>
    public string? ScreenshotFileId { get; init; }

    /// <summary>The system prompt in force (the action schema + hard refusal rules).</summary>
    public string SystemPrompt { get; init; } = string.Empty;

    /// <summary>The vetted correct action as the JSON the model should have emitted (the label).</summary>
    public string CorrectActionJson { get; init; } = string.Empty;

    /// <summary>When the step was captured (UTC).</summary>
    public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// How this sample was vetted: <c>"approved"</c> (a human explicitly approved the step)
    /// or <c>"success"</c> (executed without error when per-action approval was off).
    /// </summary>
    public string Source { get; init; } = "approved";
}
