namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;

/// <summary>
/// Configuration for the LoRA <b>grounding fine-tuning</b> pipeline — capturing vetted
/// computer-use steps as supervised training data and (offline) training a LoRA adapter
/// from them. Bound from the "GroundingTraining" configuration section.
///
/// DISABLED BY DEFAULT: the recorder writes nothing, the stats/run endpoints return 501,
/// and no training can be kicked off until an operator explicitly enables it. The actual
/// training is LIVE/OFFLINE (it needs a loaded base model + compute) and is isolated behind
/// <see cref="IGroundingAdapterTrainerPort"/>; everything else (recording, gating,
/// orchestration, registration) is CI-testable with no model.
///
/// The produced adapter is registered through the EXISTING LoRA hot-swap feature
/// (<c>ILoraAdapterService</c>), so a successfully trained grounding adapter becomes
/// hot-swappable into a custom agent / the computer-use loop exactly like an Admin-uploaded
/// one. Registration therefore also requires <c>Lora:Enabled</c> to be true.
/// </summary>
public sealed class GroundingTrainingOptions
{
    public const string SectionName = "GroundingTraining";

    /// <summary>Master switch. False (default) = the grounding-training pipeline is off everywhere.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Root directory the captured raw samples (JSONL) are stored under, in per-tenant
    /// subdirectories. Empty (default) resolves to <c>&lt;current-dir&gt;/App_Data/grounding</c>
    /// at runtime (see <see cref="ResolveDatasetRoot"/>). Server-controlled — a per-tenant
    /// subdirectory named by the tenant id is created under it; the client path is never used.
    /// </summary>
    public string DatasetPath { get; set; } = string.Empty;

    /// <summary>
    /// Root directory produced adapter files are written to before registration. Empty
    /// (default) resolves to <c>&lt;DatasetRoot&gt;/adapters</c> (see
    /// <see cref="ResolveAdapterOutputRoot"/>). A per-tenant subdirectory is created under it.
    /// </summary>
    public string AdapterOutputPath { get; set; } = string.Empty;

    /// <summary>Refuse to train until at least this many vetted samples exist for the tenant.</summary>
    public int MinSamplesToTrain { get; set; } = 50;

    /// <summary>LoRA rank knob passed to the trainer port.</summary>
    public int Rank { get; set; } = 8;

    /// <summary>LoRA alpha knob passed to the trainer port.</summary>
    public float Alpha { get; set; } = 16;

    /// <summary>Training epochs knob passed to the trainer port.</summary>
    public int Epochs { get; set; } = 1;

    /// <summary>Learning-rate knob passed to the trainer port.</summary>
    public float LearningRate { get; set; } = 1e-4f;

    /// <summary>Effective dataset root; defaults to &lt;current-dir&gt;/App_Data/grounding when unset.</summary>
    public string ResolveDatasetRoot() => string.IsNullOrWhiteSpace(DatasetPath)
        ? Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "grounding")
        : DatasetPath;

    /// <summary>Effective adapter-output root; defaults to &lt;DatasetRoot&gt;/adapters when unset.</summary>
    public string ResolveAdapterOutputRoot() => string.IsNullOrWhiteSpace(AdapterOutputPath)
        ? Path.Combine(ResolveDatasetRoot(), "adapters")
        : AdapterOutputPath;
}
