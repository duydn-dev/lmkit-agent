using LmKitOmniApi.Application.McpServers.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.McpServers.Handlers;

public class ListMcpServersQueryHandler : IRequestHandler<ListMcpServersQuery, List<McpServerSummaryDto>>
{
    private readonly HermesDbContext _db;

    public ListMcpServersQueryHandler(HermesDbContext db)
    {
        _db = db;
    }

    public async Task<List<McpServerSummaryDto>> Handle(ListMcpServersQuery request, CancellationToken cancellationToken)
    {
        return await _db.ExternalMcpServers
            .Where(server => server.TenantId == request.TenantId)
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
            .ToListAsync(cancellationToken);
    }
}
