using MediatR;

namespace LmKitOmniApi.Application.ApiKeys.Commands;

/// <summary>
/// Mints a new API key for the caller. The raw secret is returned exactly once in the
/// result; only its SHA-256 hash is persisted.
/// </summary>
public sealed class CreateApiKeyCommand : IRequest<CreateApiKeyResult>
{
    public required Guid TenantId { get; init; }
    public required Guid UserId { get; init; }
    public string? Name { get; init; }
    /// <summary>1..365; defaults to <see cref="ApiKeyRules.DefaultExpiresInDays"/>.</summary>
    public int? ExpiresInDays { get; init; }
    /// <summary>0 (unlimited, default) .. 1,000,000 total requests.</summary>
    public int? MaxRequests { get; init; }
}
