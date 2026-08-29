using MediatR;

namespace LmKitOmniApi.Application.Users.Commands;

/// <summary>
/// JSON-bound request body for PUT /api/users/{id}/role. Moved verbatim from UsersController.cs.
/// </summary>
public class UpdateRoleRequest
{
    public string Role { get; set; } = string.Empty;
}

public class UpdateUserRoleCommand : IRequest<UpdateUserRoleResult>
{
    /// <summary>Set by the controller from claims — never from the request body.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The authenticated admin performing the change (for the self-demote guard).</summary>
    public Guid ActorUserId { get; set; }

    /// <summary>The user being modified (route id).</summary>
    public Guid TargetUserId { get; set; }

    public string Role { get; set; } = string.Empty;
}

public sealed class UpdateUserRoleResult
{
    public UserMutationStatus Status { get; init; }

    /// <summary>Exact Vietnamese message for 404/400 responses.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Success message ("Cập nhật quyền thành công.").</summary>
    public string? Message { get; init; }

    /// <summary>The canonicalized role after the update (echoed in the success payload).</summary>
    public string? Role { get; init; }
}
