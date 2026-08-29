namespace LmKitOmniApi.Application.Users;

/// <summary>
/// Shared user-management rules moved out of <c>UsersController</c> so every
/// handler validates against the same allowlist.
/// </summary>
internal static class UserRules
{
    internal static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase) { "Admin", "Member" };
}

/// <summary>
/// Outcome discriminator the controller maps back onto the original HTTP contract
/// (Success → 200, NotFound → 404, ValidationFailed → 400).
/// </summary>
public enum UserMutationStatus
{
    Success,
    NotFound,
    ValidationFailed
}
