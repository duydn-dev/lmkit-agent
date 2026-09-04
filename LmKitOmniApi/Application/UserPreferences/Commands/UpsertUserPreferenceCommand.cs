using MediatR;
using LmKitOmniApi.Application.UserPreferences.Queries;

namespace LmKitOmniApi.Application.UserPreferences.Commands;

/// <summary>
/// Upserts the caller's custom instructions (exactly one row per tenant+user). Both
/// fields are optional and length-validated; whitespace-only collapses to null.
/// </summary>
public sealed class UpsertUserPreferenceCommand : IRequest<UpsertUserPreferenceResult>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string? AboutUser { get; set; }
    public string? ResponseStyle { get; set; }
}

/// <summary>
/// Outcome of an upsert. <see cref="ErrorMessage"/> non-null means a length rule was
/// violated and the controller returns 400 with that Vietnamese message; otherwise
/// <see cref="Preferences"/> is the saved, normalized state.
/// </summary>
public sealed class UpsertUserPreferenceResult
{
    public CustomInstructionsDto? Preferences { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// OPTIONAL JSON body of <c>PUT /api/user/custom-instructions</c>. Wire shape:
/// <c>{ "aboutUser": string?, "responseStyle": string? }</c>. An empty object clears
/// both fields.
/// </summary>
public sealed class UpsertCustomInstructionsRequest
{
    public string? AboutUser { get; set; }
    public string? ResponseStyle { get; set; }
}
