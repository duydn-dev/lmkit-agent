using LmKitOmniApi.Application.Users.Commands;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Users.Handlers;

public class CreateUserCommandHandler : IRequestHandler<CreateUserCommand, CreateUserResult>
{
    private readonly HermesDbContext _dbContext;
    private readonly ILogger<CreateUserCommandHandler> _logger;

    public CreateUserCommandHandler(HermesDbContext dbContext, ILogger<CreateUserCommandHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<CreateUserResult> Handle(CreateUserCommand request, CancellationToken cancellationToken)
    {
        // Validation rules and their order are copied unchanged from the original controller action.
        if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password) || string.IsNullOrEmpty(request.FullName))
            return ValidationFailed("Email, Password và FullName là bắt buộc.");
        if (request.Password.Length is < 12 or > 128)
            return ValidationFailed("Mật khẩu phải có từ 12 đến 128 ký tự.");
        if (request.Email.Length > 320 || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(request.Email))
            return ValidationFailed("Email không hợp lệ.");

        var role = string.IsNullOrWhiteSpace(request.Role) ? "Member" : request.Role.Trim();
        if (!UserRules.AllowedRoles.Contains(role))
            return ValidationFailed("Role chỉ có thể là Admin hoặc Member.");

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        // Deliberately NOT tenant-scoped: emails are unique across the whole system (original behavior).
        if (await _dbContext.Users.AnyAsync(u => u.Email.ToLower() == normalizedEmail, cancellationToken))
            return ValidationFailed("Email này đã tồn tại trong hệ thống.");

        var newUser = new User
        {
            Email = normalizedEmail,
            Username = normalizedEmail.Split('@')[0],
            FullName = request.FullName.Trim(),
            Role = UserRules.AllowedRoles.First(candidate => candidate.Equals(role, StringComparison.OrdinalIgnoreCase)),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            IsActive = true,
            TenantId = request.TenantId
        };

        _dbContext.Users.Add(newUser);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Admin created new user {Email} with role {Role}", newUser.Email, newUser.Role);

        return new CreateUserResult
        {
            Status = UserMutationStatus.Success,
            User = new CreatedUserDto
            {
                Id = newUser.Id,
                Email = newUser.Email,
                FullName = newUser.FullName,
                Role = newUser.Role,
                IsActive = newUser.IsActive,
                TenantId = newUser.TenantId
            }
        };
    }

    private static CreateUserResult ValidationFailed(string message) =>
        new() { Status = UserMutationStatus.ValidationFailed, ErrorMessage = message };
}
