using MediatR;

namespace LmKitOmniApi.Application.ApiKeys.Commands;

/// <summary>
/// Revokes one of the caller's own API keys (stamps <c>RevokedAtUtc</c>). Returns
/// <c>false</c> when the key does not exist for this tenant/user — foreign keys are
/// indistinguishable from missing ones.
/// </summary>
public sealed class RevokeApiKeyCommand : IRequest<bool>
{
    public required Guid TenantId { get; init; }
    public required Guid UserId { get; init; }
    public required Guid KeyId { get; init; }
}
