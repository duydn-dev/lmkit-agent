using LMKit.Finetuning;
using LMKit.Model;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Services;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;

/// <summary>
/// Default, LIVE-ONLY <see cref="IGroundingAdapterTrainerPort"/> — the single place LM-Kit's
/// fine-tuning API is touched. It loads the chat model, takes the chat inference lease (same
/// acquire discipline as the rest of the app), turns each raw <see cref="GroundingSample"/>
/// into a <c>ChatHistory</c> (System prompt → User "task + elements" → Assistant "correct
/// action JSON"), feeds them to <c>LoraFinetuning</c>, and writes the produced adapter to
/// disk. Requires a real native model + compute, so it is NOT run in CI — the recorder /
/// service / controller are all tested with a fake port.
///
/// KEY CONSTRAINT: <c>ChatHistory</c> has only <c>ctor(LM model)</c>, so a training sample
/// can only be built with a loaded model. That construction lives HERE and nowhere in a
/// CI-testable path — hence the raw, model-free <see cref="GroundingSample"/> DTO the
/// recorder persists.
/// </summary>
public sealed class LmKitGroundingAdapterTrainerPort : IGroundingAdapterTrainerPort
{
    private readonly LmModelManager _modelManager;
    private readonly ILogger<LmKitGroundingAdapterTrainerPort> _logger;

    public LmKitGroundingAdapterTrainerPort(
        LmModelManager modelManager,
        ILogger<LmKitGroundingAdapterTrainerPort> logger)
    {
        _modelManager = modelManager;
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

        // Building ChatHistory + training mutates the shared chat model, so hold the chat
        // inference lease for the whole run (mirrors LoRA apply / the orchestrator).
        var model = await _modelManager.GetChatModelAsync(ct: ct);
        await using var lease = await _modelManager.AcquireChatInferenceAsync(ct);

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

        var added = 0;
        foreach (var sample in samples)
        {
            ct.ThrowIfCancellationRequested();

            // ChatHistory(LM) is the model-bound construction the raw DTO defers to us.
            var history = new ChatHistory(model);
            history.AddMessage(AuthorRole.System, sample.SystemPrompt);
            history.AddMessage(AuthorRole.User, sample.TaskGoal + "\n" + sample.ElementsText);
            history.AddMessage(AuthorRole.Assistant, sample.CorrectActionJson);
            finetuning.AddTrainingData(history);
            added++;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(adapterOutputPath)!);
        _logger.LogInformation(
            "Training grounding LoRA adapter from {Samples} samples (rank {Rank}, alpha {Alpha}, epochs {Epochs}) → {Path}.",
            added, opts.Rank, opts.Alpha, opts.Epochs, adapterOutputPath);

        finetuning.TrainToAdapter(adapterOutputPath);

        return new GroundingTrainResult(adapterOutputPath, added);
    }
}
