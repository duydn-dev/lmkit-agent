namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;

/// <summary>Result of a successful adapter-training run: where the adapter was written and how many samples fed it.</summary>
public sealed record GroundingTrainResult(string AdapterPath, int SampleCount);

/// <summary>
/// The LIVE/OFFLINE training seam — the ONLY place that turns raw <see cref="GroundingSample"/>
/// records into <c>LMKit.TextGeneration.Chat.ChatHistory</c> objects (which require a loaded
/// <c>LM</c>) and runs <c>LMKit.Finetuning.LoraFinetuning</c> to produce a LoRA adapter file.
///
/// Because it needs a base model + compute it is exercised in a running deployment, NOT in
/// CI — every CI-testable code path above it (recorder, service, controller) uses a fake
/// implementation of this interface. The default live implementation is
/// <see cref="LmKitGroundingAdapterTrainerPort"/>.
/// </summary>
public interface IGroundingAdapterTrainerPort
{
    /// <summary>
    /// Trains a LoRA adapter from <paramref name="samples"/> using the knobs in
    /// <paramref name="opts"/> and writes it to <paramref name="adapterOutputPath"/>,
    /// returning that path and the sample count. Throws on failure (the caller wraps it).
    /// </summary>
    Task<GroundingTrainResult> TrainAsync(
        IReadOnlyList<GroundingSample> samples,
        GroundingTrainingOptions opts,
        string adapterOutputPath,
        CancellationToken ct = default);
}
