using MediatR;

namespace LmKitOmniApi.Application.Approvals.Queries;

/// <summary>
/// Lists the caller's pending task approvals, newest first. Approvals past their
/// deadline are excluded even when the background sweeper has not yet marked them
/// Expired — the list must only ever offer actions that are still executable.
/// </summary>
public class GetPendingApprovalsQuery : IRequest<List<PendingApprovalDto>>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
}

/// <summary>
/// Projection returned by <see cref="GetPendingApprovalsQuery"/>. Property
/// names and declaration order intentionally mirror the previous
/// anonymous-type projection so the serialized JSON shape is unchanged.
/// </summary>
public class PendingApprovalDto
{
    public Guid Id { get; set; }
    public string ActionName { get; set; } = string.Empty;
    /// <summary>
    /// The decrypted action payload (e.g. the SQL a write tool wants to run) so a
    /// human can meaningfully approve/reject it. Owner-scoped: only ever returned to
    /// the user the approval belongs to. Capped for display.
    /// </summary>
    public string Details { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// When this approval stops being answerable. Always in the future for a row in this
    /// list. Carried so a client can show the remaining time and warn before the window
    /// closes; this round only makes the API honest and does not change the UI.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// The chat session this approval was raised in — straight off the row. Carried so a
    /// surface that approves outside the originating chat (the Approvals page) can write the
    /// tool output back into the conversation that asked for it, instead of rediscovering the
    /// session by full-text-searching message content for this approval's GUID.
    /// </summary>
    public Guid ChatSessionId { get; set; }

    /// <summary>
    /// True when <see cref="ChatSessionId"/> names a session the user can actually open and
    /// continue. False for the two substrates that carry a REAL <see cref="ChatSessionId"/> but
    /// are excluded from every chat list and search: the hidden <c>IsAgentRun</c> session behind
    /// an agent run or a computer-use gate, and a temporary (<c>IsEphemeral</c>) chat, which
    /// persists nothing to continue.
    ///
    /// <para>This flag is why the id alone does not replace the search it removes: without it a
    /// client would post a continuation into a hidden substrate and then link the user to a
    /// session that never appears in their chat list.</para>
    /// </summary>
    public bool IsChatSession { get; set; }

    /// <summary>
    /// Title of that session, so a client can name the conversation it wrote into. Empty unless
    /// <see cref="IsChatSession"/>.
    /// </summary>
    public string ChatSessionTitle { get; set; } = string.Empty;
}
