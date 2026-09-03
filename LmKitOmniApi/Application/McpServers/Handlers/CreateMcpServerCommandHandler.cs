using LmKitOmniApi.Application.McpServers.Commands;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.Mcp;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using MediatR;

namespace LmKitOmniApi.Application.McpServers.Handlers;

public class CreateMcpServerCommandHandler : IRequestHandler<CreateMcpServerCommand, SaveMcpServerResult>
{
    private readonly HermesDbContext _db;
    private readonly ToolSandboxService _sandbox;
    private readonly McpHeaderProtector _protector;
    private readonly McpClientService _mcp;

    public CreateMcpServerCommandHandler(HermesDbContext db, ToolSandboxService sandbox, McpHeaderProtector protector, McpClientService mcp)
    {
        _db = db;
        _sandbox = sandbox;
        _protector = protector;
        _mcp = mcp;
    }

    public async Task<SaveMcpServerResult> Handle(CreateMcpServerCommand request, CancellationToken cancellationToken)
    {
        var validation = await McpServerRules.ValidateAsync(_db, _sandbox, request.TenantId, null, request, cancellationToken);
        if (validation is not null) return validation;

        var authMode = McpServerRules.NormalizeAuthMode(request.AuthMode)!; // validated above
        var server = new ExternalMcpServer
        {
            TenantId = request.TenantId,
            Name = request.Name.Trim().ToLowerInvariant(),
            Url = request.Url.TrimEnd('/'),
            HeadersJson = McpServerRules.ProtectHeaders(_protector, request.Headers),
            IsActive = request.IsActive,
            TrustReadOnlyAnnotations = request.TrustReadOnlyAnnotations,
            AuthMode = authMode
        };
        if (authMode is McpOAuthTokenProvider.ClientCredentialsMode or McpOAuthTokenProvider.AuthorizationCodeMode)
        {
            server.OAuthClientId = request.OAuthClientId!.Trim();
            server.OAuthTokenUrl = request.OAuthTokenUrl!.Trim();
            server.OAuthScopes = string.IsNullOrWhiteSpace(request.OAuthScopes) ? null : request.OAuthScopes.Trim();
            // Validation guarantees a non-blank secret on create.
            server.OAuthClientSecretProtected = McpServerRules.ProtectSecret(_protector, request.OAuthClientSecret!);
            // The authorize endpoint is only meaningful for the per-user authorization-code grant.
            if (authMode == McpOAuthTokenProvider.AuthorizationCodeMode)
                server.OAuthAuthorizeUrl = request.OAuthAuthorizeUrl!.Trim();
        }
        _db.ExternalMcpServers.Add(server);
        await _db.SaveChangesAsync(cancellationToken);
        await _mcp.InvalidateTenantCacheAsync(request.TenantId, cancellationToken);

        return new SaveMcpServerResult
        {
            Status = McpServerMutationStatus.Success,
            Server = new CreatedMcpServerDto
            {
                Id = server.Id,
                Name = server.Name,
                Url = server.Url,
                IsActive = server.IsActive,
                TrustReadOnlyAnnotations = server.TrustReadOnlyAnnotations,
                AuthMode = server.AuthMode
            }
        };
    }
}
