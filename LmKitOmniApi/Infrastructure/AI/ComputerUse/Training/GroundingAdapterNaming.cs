using LmKitOmniApi.Infrastructure.AI.Lora;

namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;

/// <summary>
/// The single source of truth linking the two halves of the grounding pipeline: the name
/// <see cref="GroundingTrainingService"/> registers a trained adapter under, and the filter
/// <see cref="ComputerUseModel"/> uses to find it again at inference time.
///
/// Keeping both in one type is the point — a produced adapter that nothing can select is exactly
/// the failure this pipeline shipped with, and <c>GroundingAdapterNamingTests</c> pins the
/// producer and the selector to each other.
/// </summary>
public static class GroundingAdapterNaming
{
    /// <summary>Name prefix every auto-trained grounding adapter carries.</summary>
    public const string Prefix = "grounding-";

    /// <summary>A fresh, unique registration name for an adapter trained at <paramref name="utc"/>.</summary>
    public static string NewName(DateTime utc) =>
        $"{Prefix}{utc:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>True when <paramref name="name"/> is an auto-trained grounding adapter's name.</summary>
    public static bool IsGroundingAdapter(string? name) =>
        name is not null && name.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// The grounding adapter to apply for <paramref name="tenantId"/>: the newest ACTIVE
    /// registration whose name says it is one, or null when the LoRA feature is off or the tenant
    /// has never trained one. <c>ListAsync</c> already orders newest-first, so "newest" needs no
    /// extra sort — retraining supersedes, and deactivating a bad adapter falls back to the one
    /// before it.
    /// </summary>
    public static async Task<Guid?> SelectNewestActiveAsync(
        ILoraAdapterService loraService,
        Guid tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loraService);
        if (!loraService.Enabled) return null;

        var registrations = await loraService.ListAsync(tenantId, ct);
        foreach (var registration in registrations)
        {
            if (registration.IsActive && IsGroundingAdapter(registration.Name))
                return registration.Id;
        }
        return null;
    }
}
