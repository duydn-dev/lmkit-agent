using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")] // Chỉ Admin mới được truy cập các API này
public class UsersController : ApiControllerBase
{
    private static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase) { "Admin", "Member" };
    private readonly HermesDbContext _dbContext;
    private readonly ILogger<UsersController> _logger;

    public UsersController(HermesDbContext dbContext, ILogger<UsersController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetUsers(CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out _)) return Unauthorized();
        var users = await _dbContext.Users
            .Where(user => user.TenantId == tenantId)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.FullName,
                u.Role,
                u.IsActive,
                u.CreatedAt,
                u.UpdatedAt,
                u.FailedLoginAttempts,
                u.LockoutEnd,
                u.TenantId
            })
            .OrderByDescending(u => u.CreatedAt)
            .ToListAsync(cancellationToken);

        return Ok(users);
    }

    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out _)) return Unauthorized();
        if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password) || string.IsNullOrEmpty(request.FullName))
            return BadRequest(new { message = "Email, Password và FullName là bắt buộc." });
        if (request.Password.Length is < 12 or > 128)
            return BadRequest(new { message = "Mật khẩu phải có từ 12 đến 128 ký tự." });
        if (request.Email.Length > 320 || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(request.Email))
            return BadRequest(new { message = "Email không hợp lệ." });

        var role = string.IsNullOrWhiteSpace(request.Role) ? "Member" : request.Role.Trim();
        if (!AllowedRoles.Contains(role))
            return BadRequest(new { message = "Role chỉ có thể là Admin hoặc Member." });

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        if (await _dbContext.Users.AnyAsync(u => u.Email.ToLower() == normalizedEmail, cancellationToken))
            return BadRequest(new { message = "Email này đã tồn tại trong hệ thống." });

        var newUser = new User
        {
            Email = normalizedEmail,
            Username = normalizedEmail.Split('@')[0],
            FullName = request.FullName.Trim(),
            Role = AllowedRoles.First(candidate => candidate.Equals(role, StringComparison.OrdinalIgnoreCase)),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            IsActive = true,
            TenantId = tenantId
        };

        _dbContext.Users.Add(newUser);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Admin created new user {Email} with role {Role}", newUser.Email, newUser.Role);

        return Ok(new
        {
            newUser.Id,
            newUser.Email,
            newUser.FullName,
            newUser.Role,
            newUser.IsActive,
            newUser.TenantId
        });
    }

    [HttpPut("{id}/role")]
    public async Task<IActionResult> UpdateRole(Guid id, [FromBody] UpdateRoleRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var actorId)) return Unauthorized();
        var user = await _dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.TenantId == tenantId, cancellationToken);
        if (user == null)
            return NotFound(new { message = "Không tìm thấy User." });

        if (string.IsNullOrWhiteSpace(request.Role) || !AllowedRoles.Contains(request.Role))
            return BadRequest(new { message = "Role chỉ có thể là Admin hoặc Member." });
        if (actorId == id && !request.Role.Equals("Admin", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Bạn không thể tự gỡ quyền Admin của chính mình." });

        user.Role = AllowedRoles.First(candidate => candidate.Equals(request.Role, StringComparison.OrdinalIgnoreCase));
        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Admin updated role for user {Email} to {Role}", user.Email, user.Role);

        return Ok(new { message = "Cập nhật quyền thành công.", role = user.Role });
    }

    [HttpPut("{id}/toggle-status")]
    public async Task<IActionResult> ToggleStatus(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var actorId)) return Unauthorized();
        var user = await _dbContext.Users.FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.TenantId == tenantId, cancellationToken);
        if (user == null)
            return NotFound(new { message = "Không tìm thấy User." });
        if (actorId == id && user.IsActive)
            return BadRequest(new { message = "Bạn không thể tự khóa tài khoản của chính mình." });

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

        return Ok(new { message = $"Đã {(user.IsActive ? "mở khóa" : "khóa")} tài khoản.", isActive = user.IsActive });
    }
}

public class CreateUserRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Role { get; set; }
}

public class UpdateRoleRequest
{
    public string Role { get; set; } = string.Empty;
}
