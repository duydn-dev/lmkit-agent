using LmKitOmniApi.Application.McpServers.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Common;

namespace LmKitOmniApi.Application.McpServers.Handlers;

public class ListMcpServersQueryHandler : IRequestHandler<ListMcpServersQuery, Common.PagedResult<McpServerSummaryDto>>
{
    private readonly HermesDbContext _db;

    public ListMcpServersQueryHandler(HermesDbContext db)
    {
        _db = db;
    }

    public async Task<Common.PagedResult<McpServerSummaryDto>> Handle(ListMcpServersQuery request, CancellationToken cancellationToken)
    {
        var query = _db.ExternalMcpServers
            .Where(server => server.TenantId == request.TenantId);

        var search = request.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search}%";
            query = query.Where(server => EF.Functions.Like(server.Name, pattern) || EF.Functions.Like(server.Url, pattern));
        }

        return await query
            .OrderBy(server => server.Name)
            .Select(server => new McpServerSummaryDto
            {
                Id = server.Id,
                Name = server.Name,
                Url = server.Url,
                IsActive = server.IsActive,
                TrustReadOnlyAnnotations = server.TrustReadOnlyAnnotations,
                HasHeaders = server.HeadersJson != null,
                AuthMode = server.AuthMode,
                OAuthClientId = server.OAuthClientId,
                OAuthTokenUrl = server.OAuthTokenUrl,
                OAuthAuthorizeUrl = server.OAuthAuthorizeUrl,
                OAuthScopes = server.OAuthScopes,
                HasOAuthSecret = server.OAuthClientSecretProtected != null,
                CreatedAtUtc = server.CreatedAtUtc,
                UpdatedAtUtc = server.UpdatedAtUtc
            })
            .ToPagedResultAsync(request.Page, request.PageSize, cancellationToken);
    }
}
