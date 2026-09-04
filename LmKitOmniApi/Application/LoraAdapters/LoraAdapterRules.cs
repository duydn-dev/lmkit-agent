using System.Globalization;
using LmKitOmniApi.Domain.Entities;

namespace LmKitOmniApi.Application.LoraAdapters;

/// <summary>
/// Shared validation + DTO mapping for LoRA adapter registrations. The register/update
/// handlers run the exact same name/scale rules; the scale bounds come from
/// <c>LoraOptions</c> (MinScale/MaxScale) and are passed in so this stays a pure,
/// options-free rules class.
/// </summary>
public static class LoraAdapterRules
{
    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 300;
    public const int MaxTargetModelIdLength = 200;

    /// <summary>
    /// Validates the mutable metadata. Returns the exact Vietnamese error message for a
    /// 400 response, or null when valid. A null <paramref name="name"/>/<paramref name="scale"/>
    /// means "unchanged" (update path) and is skipped — the register handler passes both.
    /// </summary>
    public static string? Validate(
        string? name,
        float? scale,
        float minScale,
        float maxScale,
        string? description = null,
        string? targetModelId = null)
    {
        if (name is not null)
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0)
                return "Tên adapter là bắt buộc.";
            if (trimmed.Length > MaxNameLength)
                return $"Tên adapter không được vượt quá {MaxNameLength} ký tự.";
        }

        if (description?.Trim() is { Length: > MaxDescriptionLength })
            return $"Mô tả không được vượt quá {MaxDescriptionLength} ký tự.";

        if (targetModelId?.Trim() is { Length: > MaxTargetModelIdLength })
            return $"Model đích không được vượt quá {MaxTargetModelIdLength} ký tự.";

        if (scale is not null)
        {
            if (float.IsNaN(scale.Value) || float.IsInfinity(scale.Value))
                return "Hệ số scale không hợp lệ.";
            if (scale.Value < minScale || scale.Value > maxScale)
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Hệ số scale phải nằm trong khoảng [{0}, {1}].",
                    minScale, maxScale);
        }

        return null;
    }

    public static LoraAdapterDto ToDto(LoraAdapterRegistration adapter) => new()
    {
        Id = adapter.Id,
        Name = adapter.Name,
        Description = adapter.Description,
        Scale = adapter.Scale,
        TargetModelId = adapter.TargetModelId,
        FileSizeBytes = adapter.FileSizeBytes,
        IsActive = adapter.IsActive,
        CreatedAtUtc = adapter.CreatedAtUtc,
        UpdatedAtUtc = adapter.UpdatedAtUtc
    };
}

/// <summary>
/// Wire shape of a LoRA adapter registration (camelCased by ASP.NET). Deliberately omits
/// <see cref="LoraAdapterRegistration.FilePath"/> — the server storage path is never
/// exposed to clients.
/// </summary>
public sealed class LoraAdapterDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public float Scale { get; set; }
    public string? TargetModelId { get; set; }
    public long FileSizeBytes { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
