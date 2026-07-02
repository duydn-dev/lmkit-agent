using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using Microsoft.AspNetCore.RateLimiting;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly HermesDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly IWebHostEnvironment _env;

    public AuthController(HermesDbContext dbContext, IConfiguration configuration, ILogger<AuthController> logger, IWebHostEnvironment env)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _logger = logger;
        _env = env;
    }

    [EnableRateLimiting("LoginPolicy")]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";

        if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password))
            return BadRequest("Email and Password are required.");

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
        
        if (user == null)
        {
            _logger.LogWarning("Failed login attempt for non-existent email {Email} from IP {IP}", request.Email, ipAddress);
            return Unauthorized(new { message = "Invalid email or password." });
        }

        // Check if account is locked out
        if (user.LockoutEnd.HasValue && user.LockoutEnd > DateTime.UtcNow)
        {
            _logger.LogWarning("Login attempt for locked account {Email} from IP {IP}", request.Email, ipAddress);
            return Unauthorized(new { message = "Tài khoản đã bị khóa tạm thời do đăng nhập sai quá nhiều lần. Vui lòng thử lại sau 15 phút." });
        }

        // M7 Fix: BCrypt Password verification with auto-upgrade for legacy plaintext passwords
        bool isPasswordValid = false;
        
        // Check if the stored password is a BCrypt hash (starts with $2a$, $2b$, or $2y$)
        bool isBCryptHash = user.PasswordHash.StartsWith("$2a$") || user.PasswordHash.StartsWith("$2b$") || user.PasswordHash.StartsWith("$2y$");

        if (isBCryptHash)
        {
            isPasswordValid = BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash);
        }
        else
        {
            // Fallback for legacy plaintext passwords (like "admin")
            if (user.PasswordHash == request.Password)
            {
                isPasswordValid = true;
                // Auto-upgrade: Hash the plaintext password and save to DB
                user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);
                await _dbContext.SaveChangesAsync();
            }
        }

        if (!isPasswordValid)
        {
            user.FailedLoginAttempts++;
            if (user.FailedLoginAttempts >= 5)
            {
                user.LockoutEnd = DateTime.UtcNow.AddMinutes(15);
                _logger.LogWarning("Account {Email} locked out due to multiple failed login attempts from IP {IP}", request.Email, ipAddress);
            }
            else
            {
                _logger.LogWarning("Failed login attempt for {Email} from IP {IP}. Attempt {Attempt}", request.Email, ipAddress, user.FailedLoginAttempts);
            }
            await _dbContext.SaveChangesAsync();
            return Unauthorized(new { message = "Invalid email or password." });
        }

        if (!user.IsActive)
        {
            _logger.LogWarning("Login attempt for disabled account {Email} from IP {IP}", request.Email, ipAddress);
            return Unauthorized(new { message = "Account is disabled." });
        }

        // Successful login: reset failed attempts
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Successful login for {Email} from IP {IP}", request.Email, ipAddress);

        var token = GenerateJwtToken(user);

        var jwtExpiration = double.Parse(_configuration.GetSection("JwtSettings")["ExpirationInMinutes"] ?? "30");
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = !_env.IsDevelopment(),
            SameSite = SameSiteMode.Lax,
            Expires = DateTime.UtcNow.AddMinutes(jwtExpiration)
        };
        Response.Cookies.Append("hermes_token", token, cookieOptions);

        return Ok(new
        {
            user.Id,
            user.Email,
            user.FullName,
            user.Role,
            user.TenantId
        });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("hermes_token");
        return Ok();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> GetCurrentUser()
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
            return Unauthorized();

        var user = await _dbContext.Users.FindAsync(userId);
        if (user == null) return NotFound();

        return Ok(new
        {
            user.Id,
            user.Email,
            user.FullName,
            user.Role,
            user.TenantId
        });
    }

    private string GenerateJwtToken(Domain.Entities.User user)
    {
        var jwtSettings = _configuration.GetSection("JwtSettings");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings["SecretKey"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("FullName", user.FullName),
            new Claim("Role", user.Role),
            new Claim("TenantId", user.TenantId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: jwtSettings["Issuer"],
            audience: jwtSettings["Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(double.Parse(jwtSettings["ExpirationInMinutes"]!)),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
