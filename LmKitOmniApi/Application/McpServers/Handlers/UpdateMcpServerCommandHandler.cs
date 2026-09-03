using LmKitOmniApi.Application.McpServers.Commands;
using LmKitOmniApi.Infrastructure.AI.Mcp;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.McpServers.Handlers;

public class UpdateMcpServerCommandHandler : IRequestHandler<UpdateMcpServerCommand, SaveMcpServerResult>
{
    private readonly HermesDbContext _db;
    private readonly ToolSandboxService _sandbox;
    private readonly McpHeaderProtector _protector;
    private readonly McpClientService _mcp;

    public UpdateMcpServerCommandHandler(HermesDbContext db, ToolSandboxService sandbox, McpHeaderProtector protector, McpClientService mcp)
    {
        _db = db;
        _sandbox = sandbox;
        _protector = protector;
        _mcp = mcp;
    }

    public async Task<SaveMcpServerResult> Handle(UpdateMcpServerCommand request, CancellationToken cancellationToken)
    {
        // Order preserved from the original action: tenant-scoped lookup FIRST, so a
        // cross-tenant id yields 404 (never 403, never a validation error).
        var server = await _db.ExternalMcpServers.FirstOrDefaultAsync(
            item => item.Id == request.ServerId && item.TenantId == request.TenantId, cancellationToken);
        if (server is null) return SaveMcpServerResult.NotFound();

        var validation = await McpServerRules.ValidateAsync(_db, _sandbox, request.TenantId, request.ServerId, request, cancellationToken);
        if (validation is not null) return validation;

        var authMode = McpServerRules.NormalizeAuthMode(request.AuthMode)!; // validated above
        if (authMode is McpOAuthTokenProvider.ClientCredentialsMode or McpOAuthTokenProvider.AuthorizationCodeMode)
        {
            // A blank secret means "keep the stored one"; reject only when neither the
            // request nor the existing record supplies a secret.
            if (string.IsNullOrWhiteSpace(request.OAuthClientSecret) &&
                string.IsNullOrWhiteSpace(server.OAuthClientSecretProtected))
                return SaveMcpServerResult.ValidationFailed("OAuth client secret is required.");
        }

        server.Name = request.Name.Trim().ToLowerInvariant();
        server.Url = request.Url.TrimEnd('/');
        server.IsActive = request.IsActive;
        server.TrustReadOnlyAnnotations = request.TrustReadOnlyAnnotations;
        if (request.ReplaceHeaders) server.HeadersJson = McpServerRules.ProtectHeaders(_protector, request.Headers);
        server.AuthMode = authMode;
        if (authMode is McpOAuthTokenProvider.ClientCredentialsMode or McpOAuthTokenProvider.AuthorizationCodeMode)
        {
            server.OAuthClientId = request.OAuthClientId!.Trim();
            server.OAuthTokenUrl = request.OAuthTokenUrl!.Trim();
            server.OAuthScopes = string.IsNullOrWhiteSpace(request.OAuthScopes) ? null : request.OAuthScopes.Trim();
            // The authorize endpoint only applies to the per-user authorization-code grant.
            server.OAuthAuthorizeUrl = authMode == McpOAuthTokenProvider.AuthorizationCodeMode
                ? request.OAuthAuthorizeUrl!.Trim()
                : null;
            if (!string.IsNullOrWhiteSpace(request.OAuthClientSecret))
                server.OAuthClientSecretProtected = McpServerRules.ProtectSecret(_protector, request.OAuthClientSecret);
        }
        else
        {
            // Switching back to Static clears the OAuth config so no stale secret lingers.
            server.OAuthClientId = null;
            server.OAuthTokenUrl = null;
            server.OAuthAuthorizeUrl = null;
            server.OAuthScopes = null;
            server.OAuthClientSecretProtected = null;
        }

        // Leaving the per-user authorization-code grant invalidates every stored per-user
        // token for this server, so purge them rather than let a later switch back resurrect
        // stale bearers.
        if (authMode != McpOAuthTokenProvider.AuthorizationCodeMode)
            await _db.McpUserOAuthTokens.Where(t => t.ServerId == server.Id).ExecuteDeleteAsync(cancellationToken);

        server.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await _mcp.InvalidateTenantCacheAsync(request.TenantId, cancellationToken);

        return new SaveMcpServerResult { Status = McpServerMutationStatus.Success };
    }
}
