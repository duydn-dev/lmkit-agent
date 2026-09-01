using System.Security.Claims;
using System.Text.Encodings.Web;
using LmKitOmniApi.Application.ApiKeys;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.Security;

/// <summary>
/// Programmatic-access authentication: validates the <c>X-Api-Key</c> header against
/// <c>TenantApiKeys</c> (which stores only SHA-256 hex digests — see
/// <see cref="ApiKeySecret"/>) with a single lookup on the unique <c>ApiKey</c> index.
///
/// Behavior contract:
/// <list type="bullet">
///   <item>No header → <c>NoResult</c>, so the request falls through to the JWT scheme.</item>
///   <item>Unknown / revoked / expired key, inactive user, or exhausted
///   <c>MaxRequests</c> budget → <c>Fail</c> (401 challenge).</item>
///   <item>Success → a principal carrying the same authoritative claims the JWT flow
///   mints (<see cref="ClaimTypes.NameIdentifier"/> + <c>TenantId</c> + <c>Role</c>)
///   plus the <c>auth_method=api_key</c> marker that lets endpoints refuse key
///   self-management from a key-authenticated caller. The role claim type is passed
///   explicitly to <see cref="ClaimsIdentity"/> because the JWT options'
///   <c>RoleClaimType = "Role"</c> mapping is per-scheme; the name claim type is
///   <see cref="ClaimTypes.NameIdentifier"/> so <c>Identity.Name</c> stays the stable
///   user id the rate-limiter partitions on.</item>
/// </list>
/// The raw key is never persisted and never logged.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string AuthMethodClaimType = "auth_method";
    public const string AuthMethodClaimValue = "api_key";
    private const string RoleClaimType = "Role";

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var presentedValues))
            return AuthenticateResult.NoResult();

        var presentedKey = presentedValues.ToString();
        if (string.IsNullOrWhiteSpace(presentedKey))
            return AuthenticateResult.NoResult();

        // Cap before hashing so abusive payloads never reach the crypto/DB path.
        if (presentedKey.Length > ApiKeySecret.MaxPresentedLength)
            return AuthenticateResult.Fail("Invalid API key.");

        var hash = ApiKeySecret.Hash(presentedKey);
        var db = Context.RequestServices.GetRequiredService<HermesDbContext>();
        var now = DateTime.UtcNow;

        // Single indexed lookup (unique index on ApiKey) joined to the owning user.
        var key = await db.TenantApiKeys.AsNoTracking()
            .Where(candidate => candidate.ApiKey == hash
                && candidate.RevokedAtUtc == null
                && candidate.ExpiresAtUtc > now
                && candidate.User != null
                && candidate.User.IsActive)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.TenantId,
                candidate.UserId,
                candidate.MaxRequests,
                candidate.UsedRequests,
                Role = candidate.User!.Role
            })
            .FirstOrDefaultAsync(Context.RequestAborted);
        if (key is null)
            return AuthenticateResult.Fail("Invalid API key.");

        if (key.MaxRequests > 0 && key.UsedRequests >= key.MaxRequests)
            return AuthenticateResult.Fail("API key request budget exhausted.");

        // Atomic usage accounting: increment only while still under budget so
        // concurrent requests cannot overshoot MaxRequests. Accounting failures are
        // deliberately ignored — availability wins over perfect counting.
        var budgetExhausted = false;
        try
        {
            var updatedRows = await db.TenantApiKeys
                .Where(candidate => candidate.Id == key.Id
                    && (candidate.MaxRequests <= 0 || candidate.UsedRequests < candidate.MaxRequests))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        candidate => candidate.UsedRequests,
                        candidate => candidate.UsedRequests + 1),
                    Context.RequestAborted);
            budgetExhausted = updatedRows == 0 && key.MaxRequests > 0;
        }
        catch (OperationCanceledException) when (Context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never include key material here; the id is safe.
            Logger.LogWarning(ex, "API key usage accounting failed for key {ApiKeyId}; the request continues.", key.Id);
        }
        if (budgetExhausted)
            return AuthenticateResult.Fail("API key request budget exhausted.");

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, key.UserId.ToString()),
            new Claim("TenantId", key.TenantId.ToString()),
            new Claim(RoleClaimType, key.Role),
            new Claim(AuthMethodClaimType, AuthMethodClaimValue)
        };
        var identity = new ClaimsIdentity(
            claims,
            authenticationType: Scheme.Name,
            nameType: ClaimTypes.NameIdentifier,
            roleType: RoleClaimType);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
