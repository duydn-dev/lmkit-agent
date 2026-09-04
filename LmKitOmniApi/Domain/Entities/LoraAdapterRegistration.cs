using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

/// <summary>
/// An Admin-uploaded LoRA adapter registration (LoRA hot-swap). Points at a
/// server-side adapter file stored under the tenant-scoped LoRA storage directory
/// (never a client-supplied path) and carries the scale it should be applied at.
/// A custom agent may reference one registration via
/// <see cref="CustomAgent.LoraAdapterId"/>; when it does, the chat orchestrator
/// applies the adapter to the shared chat model for the duration of that request's
/// inference and removes it immediately afterwards.
///
/// Tenant-scoped: uploads live in a per-tenant subdirectory and every read/write is
/// filtered by <see cref="TenantId"/>. The whole feature is OFF BY DEFAULT (see
/// <see cref="Infrastructure.AI.Lora.LoraOptions"/>).
/// </summary>
[Table("lora_adapter_registrations")]
public sealed class LoraAdapterRegistration
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    /// <summary>Human-readable name, unique within a tenant (see the unique index).</summary>
    [MaxLength(80)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Description { get; set; }

    /// <summary>
    /// Absolute server path to the adapter file, always inside the tenant-scoped LoRA
    /// storage directory. Server-generated — never derived from the uploaded file name.
    /// </summary>
    [MaxLength(1024)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Scale the adapter is applied at, clamped to the configured [MinScale, MaxScale].</summary>
    public float Scale { get; set; } = 1.0f;

    /// <summary>Optional base chat model id this adapter was trained against.</summary>
    [MaxLength(200)]
    public string? TargetModelId { get; set; }

    /// <summary>Size of the stored adapter file in bytes.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>When false the adapter is never applied, even if referenced by an agent.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
