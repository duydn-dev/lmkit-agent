using MediatR;

namespace LmKitOmniApi.Application.Users.Commands;

public class ToggleUserStatusCommand : IRequest<ToggleUserStatusResult>
{
    /// <summary>Set by the controller from claims — never from the request body.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The authenticated admin performing the change (for the self-disable guard).</summary>
    public Guid ActorUserId { get; set; }

    /// <summary>The user being toggled (route id).</summary>
    public Guid TargetUserId { get; set; }
}

public sealed class ToggleUserStatusResult
{
    public UserMutationStatus Status { get; init; }

    /// <summary>Exact Vietnamese message for 404/400 responses.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Success message ("Đã mở khóa/khóa tài khoản.").</summary>
    public string? Message { get; init; }

    /// <summary>The user's active flag after the toggle (echoed in the success payload).</summary>
    public bool IsActive { get; init; }
}
