namespace LmKitOmniApi.Infrastructure.AI.Lora;

/// <summary>
/// Configuration for the LoRA hot-swap feature. Bound from the "Lora" section.
///
/// DISABLED BY DEFAULT: applying arbitrary adapter files to the shared chat model is a
/// privileged operation (Admin-uploaded weights that alter model behavior), so the whole
/// feature only runs when an operator explicitly enables it — same gating shape as the
/// Python/browser/web-read tools and the external-database agent. When disabled, the
/// upload/mutation endpoints return 501 and the orchestrator never applies an adapter.
/// </summary>
public sealed class LoraOptions
{
    public const string SectionName = "Lora";

    /// <summary>Master switch. False (default) = the LoRA feature is off.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Root directory adapter files are stored under, in per-tenant subdirectories.
    /// Empty (default) resolves to <c>&lt;current-dir&gt;/App_Data/lora</c> at runtime.
    /// </summary>
    public string AdapterStoragePath { get; set; } = string.Empty;

    /// <summary>Hard cap on a single uploaded adapter file, in bytes (default 512 MB).</summary>
    public long MaxAdapterBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Scale used when a registration does not specify one.</summary>
    public float DefaultScale { get; set; } = 1.0f;

    /// <summary>Lower bound the applied scale is clamped/validated against.</summary>
    public float MinScale { get; set; } = 0f;

    /// <summary>Upper bound the applied scale is clamped/validated against.</summary>
    public float MaxScale { get; set; } = 2.0f;
}
