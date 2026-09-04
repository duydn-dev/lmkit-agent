using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.UserPreferences.Queries;
using LmKitOmniApi.Infrastructure.Data;

namespace LmKitOmniApi.Application.UserPreferences.Handlers;

public sealed class GetUserPreferenceQueryHandler : IRequestHandler<GetUserPreferenceQuery, CustomInstructionsDto>
{
    private readonly HermesDbContext _dbContext;

    public GetUserPreferenceQueryHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CustomInstructionsDto> Handle(GetUserPreferenceQuery request, CancellationToken cancellationToken)
    {
        var dto = await _dbContext.UserPreferences
            .AsNoTracking()
            .Where(p => p.TenantId == request.TenantId && p.UserId == request.UserId)
            .Select(p => new CustomInstructionsDto
            {
                AboutUser = p.AboutUser,
                ResponseStyle = p.ResponseStyle,
                UpdatedAtUtc = p.UpdatedAtUtc
            })
            .FirstOrDefaultAsync(cancellationToken);

        // No row yet → the contract's "empty object" (all-null DTO), never a 404.
        return dto ?? new CustomInstructionsDto();
    }
}
