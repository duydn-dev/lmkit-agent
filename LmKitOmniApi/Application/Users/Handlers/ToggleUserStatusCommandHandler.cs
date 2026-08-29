using LmKitOmniApi.Application.Users.Commands;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Users.Handlers;

public class ToggleUserStatusCommandHandler : IRequestHandler<ToggleUserStatusCommand, ToggleUserStatusResult>
{
    private readonly HermesDbContext _dbContext;
    private readonly ILogger<ToggleUserStatusCommandHandler> _logger;

    public ToggleUserStatusCommandHandler(HermesDbContext dbContext, ILogger<ToggleUserStatusCommandHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<ToggleUserStatusResult> Handle(ToggleUserStatusCommand request, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users.FirstOrDefaultAsync(
            candidate => candidate.Id == request.TargetUserId && candidate.TenantId == request.TenantId,
            cancellationToken);
        if (user == null)
            return new ToggleUserStatusResult { Status = UserMutationStatus.NotFound, ErrorMessage = "Không tìm thấy User." };
        if (request.ActorUserId == request.TargetUserId && user.IsActive)
            return new ToggleUserStatusResult { Status = UserMutationStatus.ValidationFailed, ErrorMessage = "Bạn không thể tự khóa tài khoản của chính mình." };

        user.IsActive = !user.IsActive;
        user.UpdatedAt = DateTime.UtcNow;

        // Reset lockout nếu mở khóa
        if (user.IsActive)
        {
            user.FailedLoginAttempts = 0;
            user.LockoutEnd = null;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Admin toggled active status for user {Email}. New status: {Status}", user.Email, user.IsActive);

        return new ToggleUserStatusResult
        {
            Status = UserMutationStatus.Success,
            Message = $"Đã {(user.IsActive ? "mở khóa" : "khóa")} tài khoản.",
            IsActive = user.IsActive
        };
    }
}
