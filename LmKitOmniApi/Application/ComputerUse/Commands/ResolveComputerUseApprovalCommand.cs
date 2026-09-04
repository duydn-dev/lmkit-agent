using MediatR;

namespace LmKitOmniApi.Application.ComputerUse.Commands;

/// <summary>
/// Resolves ONE pending computer-use action approval by recording the human's decision
/// on the existing <c>TaskApproval</c> row — WITHOUT routing anything through the generic
/// tool dispatcher. This is the dedicated resolution path for computer-use: the action
/// itself executes inside the streaming loop once <see cref="ComputerUse.ComputerUseApprovalGate"/>
/// observes the approved status, so this command only flips the row's status (an atomic,
/// owner-scoped, approve-once claim), never executes a tool.
/// </summary>
public sealed class ResolveComputerUseApprovalCommand : IRequest<ResolveComputerUseApprovalOutcome>
{
    public Guid ApprovalId { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public bool Approve { get; set; }
    public string? Comment { get; set; }
}

public enum ResolveComputerUseApprovalOutcome
{
    /// <summary>No computer-use approval with this id is visible to the caller — 404.</summary>
    NotFound,

    /// <summary>The approval exists but was no longer Pending when the atomic claim ran — 409.</summary>
    Conflict,

    /// <summary>The decision was recorded — 200.</summary>
    Resolved,
}
