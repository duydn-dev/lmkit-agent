using MediatR;

namespace LmKitOmniApi.Application.Users.Commands;

/// <summary>
/// JSON-bound request body for POST /api/users. Moved verbatim from UsersController.cs —
/// property names/casing are the wire contract and must not change.
/// </summary>
public class CreateUserRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Role { get; set; }
}

public class CreateUserCommand : IRequest<CreateUserResult>
{
    /// <summary>Set by the controller from the authenticated principal's claims — never from the request body.</summary>
    public Guid TenantId { get; set; }

    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Role { get; set; }
}

public sealed class CreateUserResult
{
    public UserMutationStatus Status { get; init; }

    /// <summary>Exact Vietnamese validation message for 400 responses (unchanged strings).</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Populated on success; serialized as-is by the controller.</summary>
    public CreatedUserDto? User { get; init; }
}

/// <summary>
/// Mirrors the anonymous success payload previously built inline (Id, Email, FullName,
/// Role, IsActive, TenantId — declaration order preserved for identical JSON).
/// </summary>
public sealed class CreatedUserDto
{
    public Guid Id { get; init; }
    public string Email { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public Guid TenantId { get; init; }
}
