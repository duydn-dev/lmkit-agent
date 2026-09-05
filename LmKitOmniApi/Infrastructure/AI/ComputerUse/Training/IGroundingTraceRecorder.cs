namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;

/// <summary>
/// Captures vetted computer-use steps as raw, model-free <see cref="GroundingSample"/>
/// records (the training-data collection half of the grounding pipeline). Persistence is
/// tenant-scoped and append-only; nothing here loads a model. When the feature is disabled
/// (<c>GroundingTraining:Enabled=false</c>) recording is a no-op and reads are empty, so the
/// recorder can always be called unconditionally from the computer-use loop.
/// </summary>
public interface IGroundingTraceRecorder
{
    /// <summary>True only when an operator has enabled the feature (GroundingTraining:Enabled).</summary>
    bool Enabled { get; }

    /// <summary>
    /// Appends one vetted sample to the tenant-scoped dataset. A no-op (persists nothing)
    /// when the feature is disabled. Best-effort and safe to await from the loop.
    /// </summary>
    Task RecordAsync(GroundingSample sample, CancellationToken ct = default);

    /// <summary>All captured samples for a tenant, oldest first. Empty when disabled or none exist.</summary>
    Task<IReadOnlyList<GroundingSample>> ReadAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>How many samples are captured for a tenant. 0 when disabled or none exist.</summary>
    Task<int> CountAsync(Guid tenantId, CancellationToken ct = default);
}
