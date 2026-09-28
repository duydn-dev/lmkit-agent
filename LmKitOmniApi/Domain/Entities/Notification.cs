using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

[Table("notifications")]
public sealed class Notification
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    [MaxLength(50)]
    public string Type { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? DocumentId { get; set; }

    [MaxLength(256)]
    public string? DocumentName { get; set; }

    public string? Error { get; set; }

    public int? ChunkCount { get; set; }

    public bool IsRead { get; set; }

    /// <summary>
    /// The agent run this notification reports on, when one exists (scheduled agent-mode
    /// tasks). Lets clients deep-link from the notification into the run's full detail —
    /// the complete result plus produced files and web sources live there, while the
    /// notification body is only a 4000-char excerpt. Owner-scoped reads: a foreign or
    /// fabricated id just 404s on the run endpoint.
    /// </summary>
    public Guid? AgentRunId { get; set; }

    /// <summary>
    /// The scheduled task that produced this notification, when it exists. Lets clients
    /// deep-link completion-mode results (which have no agent run row) and generic errors
    /// back to the Lịch screen entry of the task that raised them. Owner-scoped reads: a
    /// foreign task id just 404s on the schedules endpoints.
    /// </summary>
    public Guid? ScheduledTaskId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
    public User? User { get; set; }
}
