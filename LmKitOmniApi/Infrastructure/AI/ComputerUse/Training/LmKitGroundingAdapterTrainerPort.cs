using LMKit.Data;
using LMKit.Finetuning;
using LMKit.Inference;
using LMKit.Model;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Services;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;

/// <summary>
/// Default, LIVE-ONLY <see cref="IGroundingAdapterTrainerPort"/> — the single place LM-Kit's
/// fine-tuning API is touched.
///
/// <para>
/// IT TRAINS THE MODEL THAT ACTUALLY DECIDES. This used to load the CHAT model
/// (<c>GetChatModelAsync</c>) while every grounding decision is made by the VISION model
/// (<see cref="ComputerUseModel"/> → <c>GetVisionModelAsync</c>, because the decision needs the
/// screenshot attachment). The adapter was therefore trained on a model that never makes the
/// decision it is meant to improve, and — LoRA adapters being shaped by the base model's layers
/// — could not even be loaded onto the vision model. Both halves now go through the vision role.
/// </para>
///
/// <para>
/// LEASE DISCIPLINE. Building <c>ChatHistory</c> and training both mutate the shared vision
/// model, so the VISION inference lease (<c>SemaphoreLimits:Vision</c>, 1 by default) is held for
/// the whole run — model load first, then <c>await using</c> the lease, which releases on every
/// path including faults and cancellation. This is the same order <see cref="ComputerUseModel"/>
/// and <c>AgentOrchestrator</c> use.
/// </para>
///
/// <para>
/// MULTIMODAL SAMPLES. <c>LoraFinetuning.AddTrainingData(ChatHistory)</c> has no modality, so it
/// can only ever produce a text-only sample. The multimodal path in LM-Kit 2026.9.0 is
/// <c>TrainingDataset.AddSample(new ChatTrainingSample(history, InferenceModality.Multimodal))</c>
/// fed through <c>LoraFinetuning.AddDataset(...)</c>, with the screenshot carried as a
/// <c>ChatHistory.Message(role, text, Attachment)</c>. That is the path taken here whenever the
/// recorded screenshot can still be found on disk and the loaded model reports
/// <c>HasVision</c>.
/// </para>
///
/// Requires a real native model + compute, so it is NOT run in CI — the recorder / service /
/// controller are all tested with a fake port, and the model-free half of the work (sample
/// planning, screenshot resolution) lives in <see cref="GroundingTrainingPlan"/> /
/// <see cref="IGroundingScreenshotLocator"/>, which ARE tested.
/// </summary>
public sealed class LmKitGroundingAdapterTrainerPort : IGroundingAdapterTrainerPort
{
    private readonly LmModelManager _modelManager;
    private readonly IGroundingScreenshotLocator _screenshots;
    private readonly ILogger<LmKitGroundingAdapterTrainerPort> _logger;

    public LmKitGroundingAdapterTrainerPort(
        LmModelManager modelManager,
        UserResourceAccessService resources,
        ILogger<LmKitGroundingAdapterTrainerPort> logger)
        : this(modelManager, new UploadsGroundingScreenshotLocator(resources), logger)
    {
    }

    internal LmKitGroundingAdapterTrainerPort(
        LmModelManager modelManager,
        IGroundingScreenshotLocator screenshots,
        ILogger<LmKitGroundingAdapterTrainerPort> logger)
    {
        _modelManager = modelManager;
        _screenshots = screenshots;
        _logger = logger;
    }

    public async Task<GroundingTrainResult> TrainAsync(
        IReadOnlyList<GroundingSample> samples,
        GroundingTrainingOptions opts,
        string adapterOutputPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterOutputPath);
        if (samples.Count == 0)
            throw new InvalidOperationException("Cannot train a grounding adapter from zero samples.");

        // The VISION model — the one ComputerUseModel asks for every grounding decision.
        var model = await _modelManager.GetVisionModelAsync(ct: ct);
        await using var lease = await _modelManager.AcquireVisionInferenceAsync(ct);

        // A text-only base cannot consume an image attachment; plan text-only rather than let
        // the attachment fail deep inside the trainer.
        var plan = GroundingTrainingPlan.Build(samples, _screenshots.Resolve, allowScreenshots: model.HasVision);
        if (!model.HasVision)
        {
            _logger.LogWarning(
                "The configured vision model ('{ModelId}') reports no vision support; grounding samples "
                + "will be trained TEXT-ONLY (the screenshot the decision is actually made from is dropped).",
                _modelManager.DefaultVisionModelId);
        }
        else if (plan.MissingScreenshotFile > 0)
        {
            _logger.LogWarning(
                "{Missing} of {Total} grounding samples were captured with a screenshot whose file is gone; "
                + "those samples train text-only.",
                plan.MissingScreenshotFile, samples.Count);
        }

        using var finetuning = new LoraFinetuning(model)
        {
            Parameters = new LoraTrainingParameters
            {
                Rank = opts.Rank,
                Alpha = opts.Alpha,
                Epochs = opts.Epochs,
                LearningRate = opts.LearningRate,
            }
        };

        // Attachments must stay alive until training has consumed the dataset, so they are
        // disposed in the finally below — NOT per-iteration.
        var attachments = new List<Attachment>();
        try
        {
            var dataset = new TrainingDataset();
            foreach (var turn in plan.Turns)
            {
                ct.ThrowIfCancellationRequested();

                // ChatHistory(LM) is the model-bound construction the raw DTO defers to us — and
                // it must be bound to the SAME model that is being trained.
                var history = new ChatHistory(model);
                history.AddMessage(AuthorRole.System, turn.SystemPrompt);

                if (turn.ScreenshotPath is not null)
                {
                    var attachment = new Attachment(turn.ScreenshotPath);
                    attachments.Add(attachment);
                    history.AddMessage(new ChatHistory.Message(AuthorRole.User, turn.UserText, attachment));
                }
                else
                {
                    history.AddMessage(AuthorRole.User, turn.UserText);
                }

                history.AddMessage(AuthorRole.Assistant, turn.AssistantText);
                dataset.AddSample(new ChatTrainingSample(history, turn.Modality));
            }

            var added = finetuning.AddDataset(dataset);
            if (added == 0)
                throw new InvalidOperationException(
                    $"LoRA fine-tuning accepted none of the {plan.Turns.Count} planned grounding samples "
                    + "(they may all exceed the model's training window).");

            Directory.CreateDirectory(Path.GetDirectoryName(adapterOutputPath)!);
            _logger.LogInformation(
                "Training grounding LoRA adapter on the VISION model from {Samples} samples "
                + "({Multimodal} with screenshot, {TextOnly} text-only; rank {Rank}, alpha {Alpha}, epochs {Epochs}) → {Path}.",
                added, plan.WithScreenshot, plan.WithoutScreenshot,
                opts.Rank, opts.Alpha, opts.Epochs, adapterOutputPath);

            // TrainToAdapter is a long, synchronous native call: run it off the request thread and
            // let cancellation ask the trainer to stop cooperatively.
            using (ct.Register(finetuning.RequestStop))
            {
                await Task.Run(() => finetuning.TrainToAdapter(adapterOutputPath), CancellationToken.None);
            }

            // RequestStop saves whatever was trained so far, so a cancelled run would otherwise
            // look like a success. Surface the cancellation and drop the partial artifact.
            if (ct.IsCancellationRequested)
            {
                TryDelete(adapterOutputPath);
                ct.ThrowIfCancellationRequested();
            }

            return new GroundingTrainResult(adapterOutputPath, added);
        }
        finally
        {
            foreach (var attachment in attachments)
            {
                try { attachment.Dispose(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose a training screenshot attachment."); }
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete the partial grounding adapter at {Path}.", path);
        }
    }
}
