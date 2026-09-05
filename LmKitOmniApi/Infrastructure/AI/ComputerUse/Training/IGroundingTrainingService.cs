namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;

/// <summary>Outcome of a <see cref="IGroundingTrainingService.TrainAsync"/> run.</summary>
public enum GroundingTrainingStatus
{
    /// <summary>The feature is off (GroundingTraining:Enabled=false).</summary>
    Disabled,

    /// <summary>Fewer captured samples than <see cref="GroundingTrainingOptions.MinSamplesToTrain"/>.</summary>
    InsufficientSamples,

    /// <summary>Trained AND registered as a hot-swappable LoRA adapter.</summary>
    Trained,

    /// <summary>Trained, but registration was skipped/failed (e.g. LoRA hot-swap feature off).</summary>
    TrainedNotRegistered,

    /// <summary>Training itself failed.</summary>
    Failed,
}

/// <summary>Full result of a grounding-training run, mapped to HTTP by the controller.</summary>
public sealed record GroundingTrainingRunResult(
    GroundingTrainingStatus Status,
    int SampleCount,
    int RequiredSamples,
    Guid? AdapterId,
    string? AdapterPath,
    string? Message)
{
    public static GroundingTrainingRunResult Disabled() =>
        new(GroundingTrainingStatus.Disabled, 0, 0, null, null, "Grounding training is disabled.");

    public static GroundingTrainingRunResult InsufficientSamples(int have, int required) =>
        new(GroundingTrainingStatus.InsufficientSamples, have, required, null, null,
            $"Not enough vetted samples to train: have {have}, need at least {required}.");

    public static GroundingTrainingRunResult Trained(Guid adapterId, string adapterPath, int sampleCount) =>
        new(GroundingTrainingStatus.Trained, sampleCount, 0, adapterId, adapterPath, null);

    public static GroundingTrainingRunResult TrainedNotRegistered(string adapterPath, int sampleCount, string message) =>
        new(GroundingTrainingStatus.TrainedNotRegistered, sampleCount, 0, null, adapterPath, message);

    public static GroundingTrainingRunResult Failed(string message) =>
        new(GroundingTrainingStatus.Failed, 0, 0, null, null, message);
}

/// <summary>
/// Orchestrates the grounding fine-tuning pipeline: read the tenant's captured samples,
/// gate on the enable flag + the minimum-sample threshold, invoke the LIVE trainer port,
/// then register the produced adapter through the EXISTING LoRA hot-swap feature so it
/// becomes hot-swappable. Fully CI-testable with a fake <see cref="IGroundingAdapterTrainerPort"/>
/// and a fake/real <c>ILoraAdapterService</c> — no model is ever loaded here.
/// </summary>
public interface IGroundingTrainingService
{
    /// <summary>True only when the feature is enabled (GroundingTraining:Enabled).</summary>
    bool Enabled { get; }

    /// <summary>Number of vetted samples captured for the tenant (0 when disabled).</summary>
    Task<int> CountSamplesAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Runs the pipeline for a tenant: gate → train (live port) → register the adapter via
    /// the LoRA hot-swap service. Returns a structured outcome; never throws for the
    /// expected disabled/insufficient/failed cases.
    /// </summary>
    Task<GroundingTrainingRunResult> TrainAsync(Guid tenantId, CancellationToken ct = default);
}
