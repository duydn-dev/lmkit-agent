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
    /// Terminal <see cref="Status"/> for an approval whose REQUESTER went away before a
    /// human answered — today only the computer-use gate, whose run can be cancelled
    /// mid-wait. Already part of the gate's denied-status vocabulary, so nothing has to
    /// learn a new word to read it as "not approved".
    /// </summary>
    public const string CancelledStatus = "Cancelled";

    /// <summary>
    /// The <see cref="ActionName"/> <c>ComputerUseApprovalGate</c> writes. It is a
    /// human-in-the-loop MARKER, not a dispatchable tool: the click / type / navigate it
    /// describes is performed by the computer-use loop inside its own browser container,
    /// against an observation only that loop holds. Named on the entity because four
    /// layers compare against it (the gate, the dedicated resolve handler, the generic
    /// approve handler and the tool dispatcher) and a typo in any one of them silently
    /// re-opens the "approve does nothing useful" hole.
    /// </summary>
    public const string ComputerUseActionName = "COMPUTER_USE";

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

    /// <summary>
    /// Snapshot of the NARROWING half of the requesting turn's
    /// <c>AgentRequestOptions</c> — its tool whitelist, its RAG document scope and its
    /// web-search switch — serialized by <c>ApprovalScopeSnapshot</c>. Null means "no
    /// snapshot": either the turn was genuinely unscoped, or the row predates this
    /// column, and in both cases the approval falls back to walking
    /// approval → session → bound custom agent exactly as before.
    ///
    /// <para><b>Why the row and not the session.</b> The scope used to be recoverable
    /// only through that walk, and deleting a custom agent NULLs every session's binding
    /// to it (<c>ChatSession.CustomAgentId</c> is <c>DeleteBehavior.SetNull</c>), so a
    /// pending approval from such a session executed with NO narrowing at all — strictly
    /// more authority than the turn that requested it. The snapshot survives the delete.</para>
    ///
    /// <para><b>Deliberately NOT encrypted</b>, unlike <see cref="ParametersJson"/>. That
    /// column holds user/model CONTENT — a SQL statement, a file path, a prompt — which is
    /// exactly what must not sit in the clear. This one holds authorization STRUCTURE: tool
    /// names from a fixed vocabulary, document ids, and a boolean, every one of which
    /// already lives unencrypted one join away in <c>custom_agents.AllowedToolsCsv</c> /
    /// <c>KnowledgeDocumentIdsCsv</c>. Encrypting it would protect nothing new and would
    /// make a SECURITY decision depend on the data-protection keyring: a rotated key turns
    /// every snapshot unreadable, and an unreadable snapshot has to fail closed, so a
    /// keyring hiccup would deny-all every pending approval in the system. Plaintext keeps
    /// the fail-closed branch reachable only by genuine corruption.</para>
    /// </summary>
    public string? RequestOptionsJson { get; set; }

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
