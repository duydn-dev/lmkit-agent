using MediatR;

namespace LmKitOmniApi.Application.ApiKeys.Queries;

/// <summary>Lists the caller's own API keys (tenant + user scoped), newest first.</summary>
public sealed class ListApiKeysQuery : IRequest<Common.PagedResult<ApiKeyDto>>
{
    public required Guid TenantId { get; init; }
    public required Guid UserId { get; init; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = Common.Paging.DefaultPageSize;
    public string? Search { get; set; }
}
