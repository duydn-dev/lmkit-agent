using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

[Table("task_approvals")]
public sealed class TaskApproval
{
    /// <summary>
    /// Terminal <see cref="Status"/> for an approval that nobody answered before
    /// <see cref="ExpiresAtUtc"/>. Named rather than spelled out at each site because
    /// the sweeper that writes it, the approve handler that refuses it and the pending
    /// query that hides it must never disagree on the spelling — the same reason
    /// <c>AgentRunStatuses</c> exists for the run lifecycle.
    /// </summary>
    public const string ExpiredStatus = "Expired";

    /// <summary>
    /// Fallback lifetime, in hours, for an approval created without consulting
    /// <c>ApprovalExpiry:TimeToLiveHours</c> — it backs the <see cref="ExpiresAtUtc"/>
    /// initializer below, so a creation site that forgets the option still produces a
    /// bounded row instead of an immortal one. <c>ApprovalExpiryOptions</c> defaults to
    /// this same value, so code and configuration cannot drift apart.
    ///
    /// <para>24 hours: long enough that an approval raised at the end of a working day
    /// survives the night and is answerable the next morning (the realistic turnaround
    /// for a human queue), short enough that the captured payload — a SQL statement, an
    /// MCP call, a file write — still refers to a situation the approver can remember.
    /// Past a day, clicking "approve" on a stale row is a blind signature.</para>
    /// </summary>
    public const int DefaultTimeToLiveHours = 24;

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

    /// <summary>
    /// When this approval stops being answerable. NOT nullable on purpose: a null would
    /// re-admit the "approval that lives forever" state this column exists to remove, so
    /// every row — including one written by a creation site that never heard of the
    /// option — carries a deadline. Past it the row is refused by the approve handler,
    /// hidden by the pending query, and swept to <see cref="ExpiredStatus"/> by
    /// <c>ApprovalExpirySweeper</c>, which also releases the agent run parked on it.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddHours(DefaultTimeToLiveHours);

    public DateTime? ResolvedAtUtc { get; set; }

    public Tenant? Tenant { get; set; }
    public User? User { get; set; }
    public ChatSession? ChatSession { get; set; }
}
