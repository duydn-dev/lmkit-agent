using MediatR;

namespace LmKitOmniApi.Application.UserPreferences.Queries;

/// <summary>
/// Reads the caller's custom instructions (tenant+user scoped). Always resolves to a
/// <see cref="CustomInstructionsDto"/> — an all-null DTO ("empty object") when the
/// user has never saved any, so the endpoint never 404s.
/// </summary>
public sealed class GetUserPreferenceQuery : IRequest<CustomInstructionsDto>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
}

/// <summary>Wire shape of the custom-instructions endpoint (camelCased by ASP.NET).</summary>
public sealed class CustomInstructionsDto
{
    public string? AboutUser { get; set; }
    public string? ResponseStyle { get; set; }

    /// <summary>Null when the user has never saved custom instructions.</summary>
    public DateTime? UpdatedAtUtc { get; set; }
}
