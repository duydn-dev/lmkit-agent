using LmKitOmniApi.Application.McpServers.Commands;
using LmKitOmniApi.Infrastructure.AI.Mcp;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.McpServers.Handlers;

public class DeleteMcpServerCommandHandler : IRequestHandler<DeleteMcpServerCommand, bool>
{
    private readonly HermesDbContext _db;
    private readonly McpClientService _mcp;

    public DeleteMcpServerCommandHandler(HermesDbContext db, McpClientService mcp)
    {
        _db = db;
        _mcp = mcp;
    }

    public async Task<bool> Handle(DeleteMcpServerCommand request, CancellationToken cancellationToken)
    {
        var deleted = await _db.ExternalMcpServers
            .Where(server => server.Id == request.ServerId && server.TenantId == request.TenantId)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0) return false;

        // Purge any per-user authorization-code tokens for this server (no DB FK cascade).
        await _db.McpUserOAuthTokens
            .Where(token => token.ServerId == request.ServerId && token.TenantId == request.TenantId)
            .ExecuteDeleteAsync(cancellationToken);

        await _mcp.InvalidateTenantCacheAsync(request.TenantId, cancellationToken);
        return true;
    }
}
