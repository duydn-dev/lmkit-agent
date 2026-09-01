using MediatR;

namespace LmKitOmniApi.Application.ApiKeys.Queries;

/// <summary>Lists the caller's own API keys (tenant + user scoped), newest first.</summary>
public sealed class ListApiKeysQuery : IRequest<IReadOnlyList<ApiKeyDto>>
{
    public required Guid TenantId { get; init; }
    public required Guid UserId { get; init; }
}
