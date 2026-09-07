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
    /// <summary>
    /// The ONLY thing an unauthenticated caller is ever told about a failed sign-in. Unknown
    /// email, wrong password and locked account all end here, with the same status, the same
    /// body and the same headers, because any difference between them is an oracle that lets a
    /// stranger enumerate which addresses have accounts.
    /// </summary>
    private const string CredentialRejection = "Invalid email or password.";

    /// <summary>
    /// Reachable only AFTER the correct password has been presented, which is why it can be
    /// specific: a caller who already proved they hold the credential learns nothing from it
    /// that they did not already know, while a stranger probing the endpoint never gets here.
    /// </summary>
    private const string LockoutNotice =
        "Tài khoản đã bị khóa tạm thời do đăng nhập sai quá nhiều lần. Vui lòng thử lại sau 15 phút.";

    private const string RefreshTokenRejection = "Refresh Token không hợp lệ hoặc đã hết hạn.";

    /// <summary>
    /// Said out loud, on purpose. Revoking the family logs the legitimate holder out, and a
    /// silent 401 would read as a glitch and teach them to retry; this tells them the session
    /// was terminated and that signing in again is the remedy.
    /// </summary>
    private const string RefreshTokenReuseNotice =
        "Phiên đăng nhập đã bị thu hồi vì Refresh Token được sử dụng lại. Vui lòng đăng nhập lại.";

    /// <summary>Five wrong passwords, then fifteen minutes. Unchanged by this file's fixes.</summary>
    private const int MaxFailedLoginAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private const string ActiveSession = "active";
    private const string RevokedSession = "revoked";

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
        var now = DateTime.UtcNow;

        // An EXPIRED lockout hands the account its full budget of attempts back. Without this,
        // the counter stays at the limit for ever (only a successful login used to clear it),
        // so the first wrong password after the window closed re-locks immediately and anyone
        // who knows the address holds the account shut at one request per fifteen minutes.
        // Serving the window is the whole penalty; it must not also be a permanent handicap.
        if (user is not null && user.LockoutEnd.HasValue && user.LockoutEnd <= now)
        {
            user.FailedLoginAttempts = 0;
            user.LockoutEnd = null;
        }

        var isLockedOut = user is not null && user.LockoutEnd.HasValue && user.LockoutEnd > now;

        // Verified on EVERY path, including the two that used to return before reaching it: a
        // missing account and a locked one. Skipping the hash there left the response TIME
        // saying what the response body no longer does. See LoginPasswordVerifier.
        var isPasswordValid = LoginPasswordVerifier.Verify(request.Password, user?.PasswordHash);

        if (!isPasswordValid)
        {
            if (user is null)
            {
                _logger.LogWarning("Failed login attempt for non-existent email {Email} from IP {IP}", request.Email, ipAddress);
            }
            else if (isLockedOut)
            {
                // Deliberately does NOT touch the counter or extend the window. Letting failed
                // attempts accumulate during a lockout would hand the same denial of service
                // back through a different door.
                _logger.LogWarning("Failed login attempt for locked account {Email} from IP {IP}", request.Email, ipAddress);
            }
            else
            {
                user.FailedLoginAttempts++;
                if (user.FailedLoginAttempts >= MaxFailedLoginAttempts)
                {
                    user.LockoutEnd = now.Add(LockoutDuration);
                    _logger.LogWarning("Account {Email} locked out due to multiple failed login attempts from IP {IP}", request.Email, ipAddress);
                }
                else
                {
                    _logger.LogWarning("Failed login attempt for {Email} from IP {IP}. Attempt {Attempt}", request.Email, ipAddress, user.FailedLoginAttempts);
                }
                await _dbContext.SaveChangesAsync();
            }

            return Unauthorized(new { message = CredentialRejection });
        }

        // Past this line the caller has PROVED they hold the account's password, so a specific
        // reason is safe: it tells the owner what is wrong without telling a stranger that the
        // address exists.
        if (isLockedOut)
        {
            _logger.LogWarning("Correct password presented for locked account {Email} from IP {IP}", request.Email, ipAddress);
            return Unauthorized(new { message = LockoutNotice });
        }

        if (!user!.IsActive)
        {
            _logger.LogWarning("Login attempt for disabled account {Email} from IP {IP}", request.Email, ipAddress);
            // Persists the expired-lockout reset above; the account cannot sign in either way,
            // but leaving a stale counter behind would outlive the deactivation.
            await _dbContext.SaveChangesAsync();
            return Unauthorized(new { message = "Account is disabled." });
        }

        // Successful login: reset failed attempts
        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Successful login for {Email} from IP {IP}", request.Email, ipAddress);

        // The session row IS the token family, and its key is fixed before the insert, so the
        // very first refresh token can already name the family it belongs to.
        var session = new Domain.Entities.UserSession
        {
            UserId = user.Id,
            SessionKey = Guid.NewGuid().ToString("N"),
            DeviceInfo = Request.Headers.UserAgent.ToString()[..Math.Min(Request.Headers.UserAgent.ToString().Length, 500)],
            IpAddress = ipAddress[..Math.Min(ipAddress.Length, 50)],
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            LastSeenAtUtc = DateTime.UtcNow
        };
        var refreshToken = RefreshTokenProtector.GenerateFor(session.Id);
        session.RefreshTokenHash = RefreshTokenProtector.Hash(refreshToken);
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
        else if (Request.Cookies.TryGetValue("hermes_refresh_token", out var refreshToken)
            && !string.IsNullOrWhiteSpace(refreshToken))
        {
            // Hashing the WHOLE cookie, family prefix included, so this keeps matching whatever
            // RefreshTokenProtector minted.
            var refreshHash = RefreshTokenProtector.Hash(refreshToken);
            session = await _dbContext.UserSessions.FirstOrDefaultAsync(candidate => candidate.RefreshTokenHash == refreshHash);
        }

        if (session != null)
        {
            session.Status = RevokedSession;
            session.RefreshTokenHash = null;
            session.RevokedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
        }

        DeleteAuthCookie("hermes_token");
        DeleteAuthCookie("hermes_refresh_token");
        return Ok();
    }

    /// <summary>
    /// Rotates the refresh token, and treats a replayed one as evidence of theft.
    ///
    /// <para><b>Families.</b> Every token minted for one login carries that login's session id
    /// (see <see cref="RefreshTokenProtector"/>), so a SUPERSEDED token still says which family
    /// it came from even though its hash was overwritten by the rotation that superseded it.
    /// That is what makes reuse detection possible here without a schema change: the family is
    /// the <c>user_sessions</c> row, and it already survives every rotation.</para>
    ///
    /// <para><b>What a replay means.</b> Two parties now hold tokens from one family and only
    /// one of them is the legitimate holder — and nothing in the request distinguishes them.
    /// OAuth 2.0 Security BCP §4.14.2 resolves that by revoking the whole grant, which is the
    /// conservative direction: the legitimate user is logged out and signs in again, whereas
    /// rejecting only the replay leaves the thief holding the token that still works.</para>
    ///
    /// <para><b>Why a concurrent legitimate refresh cannot revoke itself.</b> Two in-flight
    /// requests carrying the CURRENT token both read the hash they presented, so neither takes
    /// the reuse branch; one wins the conditional rotation below and the other simply loses it.
    /// Losing that race is NOT treated as reuse — it is the one outcome a well-behaved client
    /// can produce — so the loser gets a plain 401 and the family survives. The revocation
    /// itself re-tests the mismatch inside its own <c>WHERE</c> clause, under the row lock, so a
    /// read that went stale between the two statements cannot revoke a family whose current
    /// token is the one being presented.</para>
    /// </summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        if (!Request.Cookies.TryGetValue("hermes_refresh_token", out var refreshToken)
            || string.IsNullOrWhiteSpace(refreshToken))
        {
            return Unauthorized(new { message = "Không tìm thấy Refresh Token." });
        }

        var refreshTokenHash = RefreshTokenProtector.Hash(refreshToken);

        // A token minted before this change carries no family and can only be found by its
        // hash — which means a replay of one is indistinguishable from a fabrication and gets
        // the plain rejection below. Those cookies age out with the first successful refresh,
        // which mints a family-carrying replacement.
        var session = RefreshTokenProtector.TryReadFamily(refreshToken, out var familyId)
            ? await _dbContext.UserSessions
                .Include(candidate => candidate.User)
                .FirstOrDefaultAsync(candidate => candidate.Id == familyId)
            : await _dbContext.UserSessions
                .Include(candidate => candidate.User)
                .FirstOrDefaultAsync(candidate => candidate.RefreshTokenHash == refreshTokenHash);

        var user = session?.User;
        if (session == null
            || user == null
            || !session.Status.Equals(ActiveSession, StringComparison.OrdinalIgnoreCase)
            || session.ExpiresAtUtc <= DateTime.UtcNow
            || !user.IsActive)
        {
            // Includes a refresh that arrives after logout or an out-of-band revocation. That
            // is an ordinary stale cookie, not evidence of theft, and it is answered as such.
            return Unauthorized(new { message = RefreshTokenRejection });
        }

        // The family is live but this is not its current token: the token was spent by an
        // earlier rotation and is being presented again.
        if (!string.Equals(session.RefreshTokenHash, refreshTokenHash, StringComparison.Ordinal))
        {
            var revokedAt = DateTime.UtcNow;
            var revoked = await _dbContext.UserSessions
                .Where(candidate => candidate.Id == session.Id
                    && candidate.Status == ActiveSession
                    && candidate.RefreshTokenHash != refreshTokenHash)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(candidate => candidate.Status, RevokedSession)
                    .SetProperty(candidate => candidate.RefreshTokenHash, (string?)null)
                    .SetProperty(candidate => candidate.RevokedAtUtc, revokedAt));

            if (revoked != 1)
            {
                // The row no longer satisfies the mismatch this branch was entered on: another
                // request revoked the family first, or it moved back under the read. Either way
                // this request revokes nothing.
                return Unauthorized(new { message = RefreshTokenRejection });
            }

            _logger.LogWarning(
                "Refresh token reuse detected for session {SessionId} (user {UserId}) from IP {IP}; "
                + "the token family has been revoked and the user must sign in again.",
                session.Id, user.Id, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown");

            // The JWT minted from this session dies with it: OnTokenValidated re-checks the
            // session on every authenticated request, so no cookie survives the revocation.
            DeleteAuthCookie("hermes_token");
            DeleteAuthCookie("hermes_refresh_token");
            return Unauthorized(new { message = RefreshTokenReuseNotice });
        }

        var newRefreshToken = RefreshTokenProtector.GenerateFor(session.Id);
        var newRefreshTokenHash = RefreshTokenProtector.Hash(newRefreshToken);
        var refreshedAt = DateTime.UtcNow;

        var rotated = await _dbContext.UserSessions
            .Where(candidate => candidate.Id == session.Id
                && candidate.RefreshTokenHash == refreshTokenHash
                && candidate.Status == ActiveSession)
            .ExecuteUpdateAsync(update => update
                .SetProperty(candidate => candidate.RefreshTokenHash, newRefreshTokenHash)
                .SetProperty(candidate => candidate.LastSeenAtUtc, refreshedAt));
        if (rotated != 1)
        {
            // Lost the rotation race to another request holding the SAME token, or the family
            // was revoked in between. Not reuse — see the remarks above — so nothing is revoked
            // here; the caller's cookie was replaced by the winner's response.
            return Unauthorized(new { message = "Refresh Token đã được sử dụng hoặc thu hồi." });
        }

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
