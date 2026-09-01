using LmKitOmniApi.Application.ApiKeys.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.ApiKeys.Handlers;

public sealed class ListApiKeysQueryHandler : IRequestHandler<ListApiKeysQuery, IReadOnlyList<ApiKeyDto>>
{
    private readonly HermesDbContext _db;

    public ListApiKeysQueryHandler(HermesDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ApiKeyDto>> Handle(ListApiKeysQuery request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        return await _db.TenantApiKeys.AsNoTracking()
            .Where(key => key.TenantId == request.TenantId && key.UserId == request.UserId)
            .OrderByDescending(key => key.CreatedAtUtc)
            .Select(key => new ApiKeyDto
            {
                Id = key.Id,
                Name = key.Name,
                MaxRequests = key.MaxRequests,
                UsedRequests = key.UsedRequests,
                ExpiresAtUtc = key.ExpiresAtUtc,
                CreatedAtUtc = key.CreatedAtUtc,
                IsActive = key.RevokedAtUtc == null && key.ExpiresAtUtc > now
            })
            .ToListAsync(cancellationToken);
    }
}
