using LmKitOmniApi.Application.ApiKeys.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Common;

namespace LmKitOmniApi.Application.ApiKeys.Handlers;

public sealed class ListApiKeysQueryHandler : IRequestHandler<ListApiKeysQuery, Common.PagedResult<ApiKeyDto>>
{
    private readonly HermesDbContext _db;

    public ListApiKeysQueryHandler(HermesDbContext db)
    {
        _db = db;
    }

    public async Task<Common.PagedResult<ApiKeyDto>> Handle(ListApiKeysQuery request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var query = _db.TenantApiKeys.AsNoTracking()
            .Where(key => key.TenantId == request.TenantId && key.UserId == request.UserId);

        var search = request.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search}%";
            query = query.Where(key => EF.Functions.Like(key.Name, pattern));
        }

        return await query
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
            .ToPagedResultAsync(request.Page, request.PageSize, cancellationToken);
    }
}
