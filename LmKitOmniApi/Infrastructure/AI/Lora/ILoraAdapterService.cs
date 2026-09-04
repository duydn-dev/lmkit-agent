using LMKit.Model;
using LmKitOmniApi.Domain.Entities;

namespace LmKitOmniApi.Infrastructure.AI.Lora;

/// <summary>
/// Application-facing surface for the LoRA hot-swap feature: Admin-driven registration
/// CRUD over tenant-scoped adapter files, plus the per-request apply seam the chat
/// orchestrator uses. LM-Kit calls are delegated to <see cref="ILoraModelPort"/>, so
/// this service is fully unit-testable with a fake port and a SQLite context.
/// </summary>
public interface ILoraAdapterService
{
    /// <summary>True only when an operator has enabled the feature (Lora:Enabled).</summary>
    bool Enabled { get; }

    /// <summary>
    /// Registers a new adapter: streams <paramref name="content"/> into a tenant-scoped
    /// file (enforcing the size cap during the copy), validates its format, and persists
    /// the registration row. Throws <see cref="LoraFeatureDisabledException"/> when the
    /// feature is off and <see cref="LoraAdapterValidationException"/> when the upload is
    /// too large, malformed, or a duplicate name for the tenant.
    /// </summary>
    Task<LoraAdapterRegistration> RegisterAsync(
        Guid tenantId,
        string name,
        string? description,
        Stream content,
        long contentLength,
        float? scale,
        string? targetModelId,
        CancellationToken ct = default);

    /// <summary>All registrations for a tenant, newest first. Empty when the feature is off.</summary>
    Task<IReadOnlyList<LoraAdapterRegistration>> ListAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>One registration, tenant-scoped, or null when missing.</summary>
    Task<LoraAdapterRegistration?> GetAsync(Guid tenantId, Guid id, CancellationToken ct = default);

    /// <summary>Deletes the registration row AND its file. Returns false when missing.</summary>
    Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default);

    /// <summary>Flips <see cref="LoraAdapterRegistration.IsActive"/>. Returns the updated row, or null when missing.</summary>
    Task<LoraAdapterRegistration?> SetActiveAsync(Guid tenantId, Guid id, bool isActive, CancellationToken ct = default);

    /// <summary>
    /// Updates the mutable metadata (name / scale / active) on a registration. Null
    /// arguments leave that field unchanged. Returns the updated row, null when missing,
    /// or throws <see cref="LoraAdapterValidationException"/> on a bad value / duplicate name.
    /// </summary>
    Task<LoraAdapterRegistration?> UpdateAsync(
        Guid tenantId,
        Guid id,
        string? name,
        float? scale,
        bool? isActive,
        CancellationToken ct = default);

    /// <summary>
    /// Applies the adapter referenced by <paramref name="loraAdapterId"/> to
    /// <paramref name="model"/> for the current request, returning a scope whose disposal
    /// removes it again. Returns <c>null</c> (a no-op) when the feature is disabled, the id
    /// is null, or the registration is missing / inactive / its file is gone — so callers
    /// can always write <c>using var scope = BeginApplyForAgent(...);</c> unconditionally.
    /// MUST be called while holding the chat inference lease.
    /// </summary>
    LoraApplyScope? BeginApplyForAgent(LM model, Guid tenantId, Guid? loraAdapterId, CancellationToken ct = default);
}
