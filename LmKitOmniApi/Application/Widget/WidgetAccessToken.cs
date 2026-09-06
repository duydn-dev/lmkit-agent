using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LmKitOmniApi.Domain.Entities;
using Microsoft.IdentityModel.Tokens;

namespace LmKitOmniApi.Application.Widget;

/// <summary>
/// Mints and validates the short-lived widget access token exchanged for a raw
/// widget key. The token carries NO user identity: only <c>sub = widget:{tenantId}</c>,
/// <c>TenantId</c>, the <c>scope</c> marker <c>widget</c> and the origin the key
/// exchange was performed from (bound at mint time, re-checked at chat time).
/// Validation pins the exact issuer/audience and requires the scope claim.
/// </summary>
public sealed class WidgetAccessTokenService
{
    public const string ScopeValue = "widget";
    public const string ScopeClaimType = "scope";
    public const string OriginClaimType = "widget_origin";
    private const string SubjectPrefix = "widget:";

    private readonly Microsoft.Extensions.Configuration.IConfiguration _configuration;

    public WidgetAccessTokenService(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string Mint(Guid tenantId, string origin, DateTime nowUtc)
    {
        var lifetimeMinutes = _configuration.GetValue("Widget:TokenLifetimeMinutes", 20);
        if (lifetimeMinutes is < 5 or > 120) lifetimeMinutes = 20;

        var (key, issuer, audience) = Materialize();
        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateJwtSecurityToken(
            issuer: issuer,
            audience: audience,
            subject: new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, SubjectPrefix + tenantId),
                new Claim("TenantId", tenantId.ToString()),
                new Claim(ScopeClaimType, ScopeValue),
                new Claim(OriginClaimType, origin)
            }),
            notBefore: nowUtc,
            expires: nowUtc.AddMinutes(lifetimeMinutes),
            issuedAt: nowUtc,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return handler.WriteToken(token);
    }

    /// <summary>
    /// Validates signature, lifetime, issuer and audience, and requires the
    /// <c>scope=widget</c> marker. Returns the principal, or null when invalid.
    /// </summary>
    public ClaimsPrincipal? Validate(string token)
    {
        try
        {
            var (key, issuer, audience) = Materialize();
            var validator = new JwtSecurityTokenHandler();
            var principal = validator.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = issuer,
                ValidAudience = audience,
                IssuerSigningKey = key,
                ClockSkew = TimeSpan.FromSeconds(30)
            }, out _);

            return principal.HasClaim(ScopeClaimType, ScopeValue) ? principal : null;
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException)
        {
            return null;
        }
    }

    private (SymmetricSecurityKey Key, string Issuer, string Audience) Materialize()
    {
        var jwtSettings = _configuration.GetSection("JwtSettings");
        var secret = jwtSettings["SecretKey"];
        if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException("JwtSettings:SecretKey must be configured with at least 32 bytes.");
        return (
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            jwtSettings["Issuer"] ?? "LmKitOmniApi",
            jwtSettings["Audience"] ?? "LmKitOmniClient");
    }
}
