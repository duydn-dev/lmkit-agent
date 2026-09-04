using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.UserPreferences.Commands;
using LmKitOmniApi.Application.UserPreferences.Queries;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;

namespace LmKitOmniApi.Application.UserPreferences.Handlers;

public sealed class UpsertUserPreferenceCommandHandler
    : IRequestHandler<UpsertUserPreferenceCommand, UpsertUserPreferenceResult>
{
    private readonly HermesDbContext _dbContext;

    public UpsertUserPreferenceCommandHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<UpsertUserPreferenceResult> Handle(UpsertUserPreferenceCommand request, CancellationToken cancellationToken)
    {
        var validationError = UserPreferenceRules.Validate(request.AboutUser, request.ResponseStyle);
        if (validationError is not null)
            return new UpsertUserPreferenceResult { ErrorMessage = validationError };

        var aboutUser = UserPreferenceRules.NormalizeOptional(request.AboutUser);
        var responseStyle = UserPreferenceRules.NormalizeOptional(request.ResponseStyle);

        // Tracked load: the single (tenant, user) row is updated in place when present,
        // inserted otherwise. The unique index keeps the insert path race-safe.
        var preference = await _dbContext.UserPreferences
            .FirstOrDefaultAsync(p => p.TenantId == request.TenantId && p.UserId == request.UserId, cancellationToken);

        if (preference is null)
        {
            preference = new UserPreference
            {
                TenantId = request.TenantId,
                UserId = request.UserId,
                AboutUser = aboutUser,
                ResponseStyle = responseStyle,
                UpdatedAtUtc = DateTime.UtcNow
            };
            _dbContext.UserPreferences.Add(preference);
        }
        else
        {
            preference.AboutUser = aboutUser;
            preference.ResponseStyle = responseStyle;
            preference.UpdatedAtUtc = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new UpsertUserPreferenceResult
        {
            Preferences = new CustomInstructionsDto
            {
                AboutUser = preference.AboutUser,
                ResponseStyle = preference.ResponseStyle,
                UpdatedAtUtc = preference.UpdatedAtUtc
            }
        };
    }
}
