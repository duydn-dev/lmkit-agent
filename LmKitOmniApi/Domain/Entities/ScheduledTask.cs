using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

/// <summary>
/// A user-defined recurring prompt run by the background worker. Scheduling is
/// deliberately preset-based (interval / daily / weekly) instead of full cron.
/// Results are delivered as <see cref="Notification"/> rows.
/// </summary>
[Table("scheduled_tasks")]
public sealed class ScheduledTask
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;

    /// <summary>"interval" | "daily" | "weekly".</summary>
    [MaxLength(20)]
    public string ScheduleKind { get; set; } = "daily";

    /// <summary>interval kind: minutes between runs (min 15).</summary>
    public int? IntervalMinutes { get; set; }

    /// <summary>daily/weekly kinds: minutes after midnight UTC (0..1439).</summary>
    public int? TimeOfDayMinutes { get; set; }

    /// <summary>weekly kind: 0 = Sunday .. 6 = Saturday (UTC).</summary>
    public int? DayOfWeek { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime NextRunUtc { get; set; }

    public DateTime? LastRunUtc { get; set; }

    /// <summary>"Succeeded" | "Failed" | "Skipped" (e.g. model unavailable).</summary>
    [MaxLength(20)]
    public string? LastStatus { get; set; }

    [MaxLength(500)]
    public string? LastError { get; set; }

    /// <summary>Worker lease: a claimed row is invisible to other replicas until this expires.</summary>
    public DateTime? ClaimedUntilUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
    public User? User { get; set; }
}
