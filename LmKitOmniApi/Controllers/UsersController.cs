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
public class UsersController : ControllerBase
{
    private readonly HermesDbContext _dbContext;
    private readonly ILogger<UsersController> _logger;

    public UsersController(HermesDbContext dbContext, ILogger<UsersController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetUsers()
    {
        var users = await _dbContext.Users
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
            .ToListAsync();

        return Ok(users);
    }

    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request)
    {
        if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password) || string.IsNullOrEmpty(request.FullName))
            return BadRequest(new { message = "Email, Password và FullName là bắt buộc." });

        if (await _dbContext.Users.AnyAsync(u => u.Email == request.Email))
            return BadRequest(new { message = "Email này đã tồn tại trong hệ thống." });

        // Lấy Tenant đầu tiên làm mặc định nếu không truyền lên
        var tenantId = request.TenantId;
        if (tenantId == Guid.Empty)
        {
            var defaultTenant = await _dbContext.Tenants.FirstOrDefaultAsync();
            if (defaultTenant == null)
                return BadRequest(new { message = "Không tìm thấy Tenant nào trong hệ thống." });
            tenantId = defaultTenant.Id;
        }

        var newUser = new User
        {
            Email = request.Email,
            Username = request.Email.Split('@')[0],
            FullName = request.FullName,
            Role = request.Role ?? "Member",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            IsActive = true,
            TenantId = tenantId
        };

        _dbContext.Users.Add(newUser);
        await _dbContext.SaveChangesAsync();

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
    public async Task<IActionResult> UpdateRole(Guid id, [FromBody] UpdateRoleRequest request)
    {
        var user = await _dbContext.Users.FindAsync(id);
        if (user == null)
            return NotFound(new { message = "Không tìm thấy User." });

        if (string.IsNullOrEmpty(request.Role))
            return BadRequest(new { message = "Role không hợp lệ." });

        user.Role = request.Role;
        user.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Admin updated role for user {Email} to {Role}", user.Email, user.Role);

        return Ok(new { message = "Cập nhật quyền thành công.", role = user.Role });
    }

    [HttpPut("{id}/toggle-status")]
    public async Task<IActionResult> ToggleStatus(Guid id)
    {
        var user = await _dbContext.Users.FindAsync(id);
        if (user == null)
            return NotFound(new { message = "Không tìm thấy User." });

        user.IsActive = !user.IsActive;
        user.UpdatedAt = DateTime.UtcNow;

        // Reset lockout nếu mở khóa
        if (user.IsActive)
        {
            user.FailedLoginAttempts = 0;
            user.LockoutEnd = null;
        }

        await _dbContext.SaveChangesAsync();
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
    public Guid TenantId { get; set; }
}

public class UpdateRoleRequest
{
    public string Role { get; set; } = string.Empty;
}
