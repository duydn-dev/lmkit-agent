using LmKitOmniApi.Application.ApiKeys.Commands;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.ApiKeys.Handlers;

public sealed class RevokeApiKeyCommandHandler : IRequestHandler<RevokeApiKeyCommand, bool>
{
    private readonly HermesDbContext _db;

    public RevokeApiKeyCommandHandler(HermesDbContext db)
    {
        _db = db;
    }

    public async Task<bool> Handle(RevokeApiKeyCommand request, CancellationToken cancellationToken)
    {
        var key = await _db.TenantApiKeys.FirstOrDefaultAsync(
            candidate => candidate.Id == request.KeyId
                && candidate.TenantId == request.TenantId
                && candidate.UserId == request.UserId,
            cancellationToken);
        if (key is null) return false;

        // Idempotent: revoking an already-revoked key keeps the original stamp.
        if (key.RevokedAtUtc is null)
        {
            key.RevokedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }
}
