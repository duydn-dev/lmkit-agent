using System.Text;
using LMKit.Inference;

namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;

/// <summary>
/// One planned training turn: the three chat messages a <see cref="GroundingSample"/> becomes,
/// plus the resolved on-disk screenshot (null when the sample had none or the file is gone) and
/// the modality that fact implies.
/// </summary>
/// <param name="SystemPrompt">System turn — the action schema + refusal rules the model saw.</param>
/// <param name="UserText">User turn — the rendered page the model had to ground against.</param>
/// <param name="AssistantText">Assistant turn — the vetted action JSON (the supervised label).</param>
/// <param name="ScreenshotPath">The picture the model saw, or null.</param>
public sealed record GroundingTrainingTurn(
    string SystemPrompt,
    string UserText,
    string AssistantText,
    string? ScreenshotPath)
{
    /// <summary>
    /// <see cref="InferenceModality.Multimodal"/> when this turn carries the screenshot the live
    /// decision is made from, otherwise <see cref="InferenceModality.Text"/>. The modality is a
    /// property of the sample, not a global switch: a dataset can legitimately mix both.
    /// </summary>
    public InferenceModality Modality =>
        ScreenshotPath is null ? InferenceModality.Text : InferenceModality.Multimodal;
}

/// <summary>
/// The model-free half of grounding fine-tuning: turns raw <see cref="GroundingSample"/> records
/// into ready-to-train turns. Deliberately holds NO <c>LMKit</c> model type, so the part that
/// used to be untestable — how a captured step becomes a training example, and whether the
/// screenshot survives the trip — is exercised in CI with no weights and no compute.
///
/// <para>
/// PROMPT FIDELITY. A fine-tune only helps if training input matches inference input, so the
/// user turn is rendered to mirror <see cref="ComputerUseModel"/>'s live user message. Two
/// fields the live prompt carries are NOT captured by the recorder and therefore cannot be
/// reproduced here: the page <c>title</c> and the prior-step <c>HISTORY</c> block. Adding them
/// means extending <see cref="GroundingSample"/> and the capture in <c>ComputerUseAgent</c>;
/// until then the training prompt is a close, not byte-identical, twin of the inference prompt.
/// </para>
/// </summary>
public sealed record GroundingTrainingPlan(
    IReadOnlyList<GroundingTrainingTurn> Turns,
    int WithScreenshot,
    int WithoutScreenshot,
    int MissingScreenshotFile)
{
    /// <summary>
    /// Builds the plan. <paramref name="resolveScreenshot"/> maps (tenant, recorded file id) to an
    /// absolute path, or null when the file cannot be found — see
    /// <see cref="IGroundingScreenshotLocator"/>. Pass a locator that always returns null to plan
    /// a deliberately text-only run.
    /// </summary>
    public static GroundingTrainingPlan Build(
        IReadOnlyList<GroundingSample> samples,
        Func<Guid, string?, string?> resolveScreenshot,
        bool allowScreenshots = true)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(resolveScreenshot);

        var turns = new List<GroundingTrainingTurn>(samples.Count);
        int withScreenshot = 0, withoutScreenshot = 0, missingFile = 0;

        foreach (var sample in samples)
        {
            if (sample is null) continue;

            string? screenshotPath = null;
            if (allowScreenshots && !string.IsNullOrWhiteSpace(sample.ScreenshotFileId))
            {
                screenshotPath = resolveScreenshot(sample.TenantId, sample.ScreenshotFileId);
                // Captured WITH a screenshot but the file is gone: the sample is still usable as a
                // text example, but it is not the sample that was recorded — count it so the run
                // can say so instead of silently degrading (LM-Kit's own dataset loaders take the
                // same line with UnresolvedImageCount).
                if (screenshotPath is null) missingFile++;
            }

            if (screenshotPath is null) withoutScreenshot++; else withScreenshot++;

            turns.Add(new GroundingTrainingTurn(
                sample.SystemPrompt,
                RenderUserTurn(sample, hasScreenshot: screenshotPath is not null),
                sample.CorrectActionJson,
                screenshotPath));
        }

        return new GroundingTrainingPlan(turns, withScreenshot, withoutScreenshot, missingFile);
    }

    /// <summary>
    /// Renders the user turn the way the live loop renders it (task → current page → the numbered
    /// element list → the closing instruction). <see cref="GroundingSample.ElementsText"/> already
    /// carries its own "INTERACTIVE ELEMENTS" header, exactly as the model saw it.
    /// </summary>
    public static string RenderUserTurn(GroundingSample sample, bool hasScreenshot)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var sb = new StringBuilder();
        sb.Append("TASK: ").Append(sample.TaskGoal).Append('\n');
        sb.Append("\nCURRENT PAGE:\n");
        sb.Append("  url: ").Append(sample.PageUrl).Append('\n');
        sb.Append('\n').Append(sample.ElementsText.TrimEnd('\n')).Append('\n');
        sb.Append(hasScreenshot
            ? "\nA screenshot of this page is attached. Respond with EXACTLY ONE action as JSON."
            : "\nRespond with EXACTLY ONE action as JSON.");
        return sb.ToString();
    }
}
