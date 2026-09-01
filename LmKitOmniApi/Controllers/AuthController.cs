using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Distributed;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly HermesDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly IDistributedCache _cache;

    public AuthController(HermesDbContext dbContext, IConfiguration configuration, ILogger<AuthController> logger, IWebHostEnvironment env, IDistributedCache cache)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _logger = logger;
        _env = env;
        _cache = cache;
    }

    [EnableRateLimiting("LoginPolicy")]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";

        if (string.IsNullOrWhiteSpace(request.Email)
            || request.Email.Length > 320
            || string.IsNullOrEmpty(request.Password)
            || request.Password.Length > 128)
            return BadRequest("A valid email and password are required.");

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);
        
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

        var isBCryptHash = user.PasswordHash.StartsWith("$2a$", StringComparison.Ordinal)
            || user.PasswordHash.StartsWith("$2b$", StringComparison.Ordinal)
            || user.PasswordHash.StartsWith("$2y$", StringComparison.Ordinal);
        var isPasswordValid = false;
        if (isBCryptHash)
        {
            try { isPasswordValid = BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash); }
            catch (BCrypt.Net.SaltParseException) { isPasswordValid = false; }
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

        var refreshToken = RefreshTokenProtector.Generate();
        var session = new Domain.Entities.UserSession
        {
            UserId = user.Id,
            SessionKey = Guid.NewGuid().ToString("N"),
            RefreshTokenHash = RefreshTokenProtector.Hash(refreshToken),
            DeviceInfo = Request.Headers.UserAgent.ToString()[..Math.Min(Request.Headers.UserAgent.ToString().Length, 500)],
            IpAddress = ipAddress[..Math.Min(ipAddress.Length, 50)],
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            LastSeenAtUtc = DateTime.UtcNow
        };
        _dbContext.UserSessions.Add(session);
        user.RefreshToken = null;
        user.RefreshTokenExpiryTime = null;
        await _dbContext.SaveChangesAsync();
        var token = GenerateJwtToken(user, session.Id);

        var jwtExpiration = double.Parse(_configuration.GetSection("JwtSettings")["ExpirationInMinutes"] ?? "30");
        var cookieOptions = BuildCookieOptions(DateTime.UtcNow.AddMinutes(jwtExpiration));
        Response.Cookies.Append("hermes_token", token, cookieOptions);

        var refreshCookieOptions = BuildCookieOptions(session.ExpiresAtUtc);
        Response.Cookies.Append("hermes_refresh_token", refreshToken, refreshCookieOptions);

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
    public async Task<IActionResult> Logout()
    {
        var jti = User.FindFirstValue(JwtRegisteredClaimNames.Jti);
        if (!string.IsNullOrWhiteSpace(jti))
        {
            var expiresAt = TryGetTokenExpiration() ?? DateTimeOffset.UtcNow.AddMinutes(30);
            if (expiresAt > DateTimeOffset.UtcNow)
            {
                await _cache.SetStringAsync(
                    $"blacklist_{jti}",
                    "revoked",
                    new DistributedCacheEntryOptions { AbsoluteExpiration = expiresAt },
                    HttpContext.RequestAborted);
            }
        }

        Domain.Entities.UserSession? session = null;
        if (Guid.TryParse(User.FindFirstValue("sid"), out var sessionId))
        {
            session = await _dbContext.UserSessions.FindAsync(sessionId);
        }
        else if (Request.Cookies.TryGetValue("hermes_refresh_token", out var refreshToken))
        {
            var refreshHash = RefreshTokenProtector.Hash(refreshToken);
            session = await _dbContext.UserSessions.FirstOrDefaultAsync(candidate => candidate.RefreshTokenHash == refreshHash);
        }

        if (session != null)
        {
            session.Status = "revoked";
            session.RefreshTokenHash = null;
            session.RevokedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
        }

        DeleteAuthCookie("hermes_token");
        DeleteAuthCookie("hermes_refresh_token");
        return Ok();
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        if (!Request.Cookies.TryGetValue("hermes_refresh_token", out var refreshToken))
        {
            return Unauthorized(new { message = "Không tìm thấy Refresh Token." });
        }

        var refreshTokenHash = RefreshTokenProtector.Hash(refreshToken);
        var session = await _dbContext.UserSessions
            .Include(candidate => candidate.User)
            .FirstOrDefaultAsync(candidate => candidate.RefreshTokenHash == refreshTokenHash);
        var user = session?.User;
        if (session == null
            || user == null
            || !session.Status.Equals("active", StringComparison.OrdinalIgnoreCase)
            || session.ExpiresAtUtc <= DateTime.UtcNow
            || !user.IsActive)
        {
            return Unauthorized(new { message = "Refresh Token không hợp lệ hoặc đã hết hạn." });
        }

        var newRefreshToken = RefreshTokenProtector.Generate();
        var newRefreshTokenHash = RefreshTokenProtector.Hash(newRefreshToken);
        var refreshedAt = DateTime.UtcNow;

        var rotated = await _dbContext.UserSessions
            .Where(candidate => candidate.Id == session.Id
                && candidate.RefreshTokenHash == refreshTokenHash
                && candidate.Status == "active")
            .ExecuteUpdateAsync(update => update
                .SetProperty(candidate => candidate.RefreshTokenHash, newRefreshTokenHash)
                .SetProperty(candidate => candidate.LastSeenAtUtc, refreshedAt));
        if (rotated != 1)
            return Unauthorized(new { message = "Refresh Token đã được sử dụng hoặc thu hồi." });

        var newJwtToken = GenerateJwtToken(user, session.Id);

        var jwtExpiration = double.Parse(_configuration.GetSection("JwtSettings")["ExpirationInMinutes"] ?? "30");
        var cookieOptions = BuildCookieOptions(DateTime.UtcNow.AddMinutes(jwtExpiration));
        Response.Cookies.Append("hermes_token", newJwtToken, cookieOptions);

        var refreshCookieOptions = BuildCookieOptions(session.ExpiresAtUtc);
        Response.Cookies.Append("hermes_refresh_token", newRefreshToken, refreshCookieOptions);

        return Ok(new { message = "Làm mới Token thành công." });
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> GetCurrentUser()
    {
        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
            return Unauthorized();

        var user = await _dbContext.Users.FindAsync(userId);
        if (user == null || !user.IsActive) return Unauthorized();

        return Ok(new
        {
            user.Id,
            user.Email,
            user.FullName,
            user.Role,
            user.TenantId
        });
    }

    private string GenerateJwtToken(Domain.Entities.User user, Guid sessionId)
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
            new Claim("sid", sessionId.ToString()),
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

    private CookieOptions BuildCookieOptions(DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = _configuration.GetValue("AuthCookies:Secure", !_env.IsDevelopment()),
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = expires
    };

    private void DeleteAuthCookie(string name) => Response.Cookies.Delete(name, new CookieOptions
    {
        Secure = _configuration.GetValue("AuthCookies:Secure", !_env.IsDevelopment()),
        SameSite = SameSiteMode.Lax,
        Path = "/"
    });

    private DateTimeOffset? TryGetTokenExpiration()
    {
        var value = User.FindFirstValue(JwtRegisteredClaimNames.Exp);
        return long.TryParse(value, out var unixSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            : null;
    }

}

public class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
