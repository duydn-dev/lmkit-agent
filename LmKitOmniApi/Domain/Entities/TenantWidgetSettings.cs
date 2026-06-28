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

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
}
