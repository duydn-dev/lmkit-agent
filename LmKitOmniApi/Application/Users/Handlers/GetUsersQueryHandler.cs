using LmKitOmniApi.Application.Users.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Users.Handlers;

public class GetUsersQueryHandler : IRequestHandler<GetUsersQuery, List<UserSummaryDto>>
{
    private readonly HermesDbContext _dbContext;

    public GetUsersQueryHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<UserSummaryDto>> Handle(GetUsersQuery request, CancellationToken cancellationToken)
    {
        return await _dbContext.Users
            .Where(user => user.TenantId == request.TenantId)
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
            .OrderByDescending(u => u.CreatedAt)
            .ToListAsync(cancellationToken);
    }
}
