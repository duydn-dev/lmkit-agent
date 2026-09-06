using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

[Table("tenant_widget_settings")]
public sealed class TenantWidgetSettings
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    [MaxLength(200)]
    public string WidgetApiKey { get; set; } = string.Empty;

    [MaxLength(500)]
    public string WidgetApiKeyHash { get; set; } = string.Empty;

    public string AllowedOriginsJson { get; set; } = "[]";

    /// <summary>Max widget chat requests per origin+minute (0 = unlimited). 0 keeps the legacy default.</summary>
    public int RequestsPerMinute { get; set; }

    /// <summary>Max widget chat requests per origin+day (0 = unlimited). 0 keeps the legacy default.</summary>
    public int RequestsPerDay { get; set; }

    [MaxLength(200)]
    public string? WidgetTitle { get; set; }

    [MaxLength(500)]
    public string? WelcomeMessage { get; set; }

    [MaxLength(50)]
    public string? BrandColor { get; set; }

    [MaxLength(500)]
    public string? LogoUrl { get; set; }

    [MaxLength(50)]
    public string Position { get; set; } = "bottom-right";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? RotatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
}
