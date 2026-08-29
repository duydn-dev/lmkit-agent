using LmKitOmniApi.Application.Users.Commands;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Users.Handlers;

public class UpdateUserRoleCommandHandler : IRequestHandler<UpdateUserRoleCommand, UpdateUserRoleResult>
{
    private readonly HermesDbContext _dbContext;
    private readonly ILogger<UpdateUserRoleCommandHandler> _logger;

    public UpdateUserRoleCommandHandler(HermesDbContext dbContext, ILogger<UpdateUserRoleCommandHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<UpdateUserRoleResult> Handle(UpdateUserRoleCommand request, CancellationToken cancellationToken)
    {
        // Order preserved from the original action: tenant-scoped lookup FIRST (an invalid role
        // for a nonexistent/cross-tenant user must still yield 404, not 400).
        var user = await _dbContext.Users.FirstOrDefaultAsync(
            candidate => candidate.Id == request.TargetUserId && candidate.TenantId == request.TenantId,
            cancellationToken);
        if (user == null)
            return new UpdateUserRoleResult { Status = UserMutationStatus.NotFound, ErrorMessage = "Không tìm thấy User." };

        // Note: deliberately no Trim here (original behavior for this endpoint).
        if (string.IsNullOrWhiteSpace(request.Role) || !UserRules.AllowedRoles.Contains(request.Role))
            return new UpdateUserRoleResult { Status = UserMutationStatus.ValidationFailed, ErrorMessage = "Role chỉ có thể là Admin hoặc Member." };
        if (request.ActorUserId == request.TargetUserId && !request.Role.Equals("Admin", StringComparison.OrdinalIgnoreCase))
            return new UpdateUserRoleResult { Status = UserMutationStatus.ValidationFailed, ErrorMessage = "Bạn không thể tự gỡ quyền Admin của chính mình." };

        user.Role = UserRules.AllowedRoles.First(candidate => candidate.Equals(request.Role, StringComparison.OrdinalIgnoreCase));
        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Admin updated role for user {Email} to {Role}", user.Email, user.Role);

        return new UpdateUserRoleResult
        {
            Status = UserMutationStatus.Success,
            Message = "Cập nhật quyền thành công.",
            Role = user.Role
        };
    }
}
