using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

[Table("task_approvals")]
public sealed class TaskApproval
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public Guid ChatSessionId { get; set; }

    [MaxLength(128)]
    public string ActionName { get; set; } = string.Empty;

    public string ParametersJson { get; set; } = string.Empty;

    [MaxLength(32)]
    public string Status { get; set; } = "Pending";

    [MaxLength(1024)]
    public string? RejectionComment { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ResolvedAtUtc { get; set; }

    public Tenant? Tenant { get; set; }
    public User? User { get; set; }
    public ChatSession? ChatSession { get; set; }
}
