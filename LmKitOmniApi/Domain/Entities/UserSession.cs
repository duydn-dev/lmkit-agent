using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

[Table("user_sessions")]
public sealed class UserSession
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    [MaxLength(256)]
    public string SessionKey { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? RefreshTokenHash { get; set; }

    [MaxLength(500)]
    public string? DeviceInfo { get; set; }

    [MaxLength(50)]
    public string? IpAddress { get; set; }

    [MaxLength(50)]
    public string Status { get; set; } = "active";

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? RevokedAtUtc { get; set; }

    public User? User { get; set; }
}
