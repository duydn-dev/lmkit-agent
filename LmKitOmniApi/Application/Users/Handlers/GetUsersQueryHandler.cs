using LmKitOmniApi.Application.Users.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Common;

namespace LmKitOmniApi.Application.Users.Handlers;

public class GetUsersQueryHandler : IRequestHandler<GetUsersQuery, Common.PagedResult<UserSummaryDto>>
{
    private readonly HermesDbContext _dbContext;

    public GetUsersQueryHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Common.PagedResult<UserSummaryDto>> Handle(GetUsersQuery request, CancellationToken cancellationToken)
    {
        var query = _dbContext.Users
            .Where(user => user.TenantId == request.TenantId);

        var search = request.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = $"%{search}%";
            query = query.Where(u => EF.Functions.Like(u.Email, pattern) || EF.Functions.Like(u.FullName, pattern));
        }

        return await query
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => new UserSummaryDto
            {
                Id = u.Id,
                Email = u.Email,
                FullName = u.FullName,
                Role = u.Role,
                IsActive = u.IsActive,
                CreatedAt = u.CreatedAt,
                UpdatedAt = u.UpdatedAt,
                FailedLoginAttempts = u.FailedLoginAttempts,
                LockoutEnd = u.LockoutEnd,
                TenantId = u.TenantId
            })
            .ToPagedResultAsync(request.Page, request.PageSize, cancellationToken);
    }
}
