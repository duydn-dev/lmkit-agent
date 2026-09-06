using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using LmKitOmniApi.Application.Widget;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.Security;

/// <summary>
/// Public-widget authentication: validates the short-lived widget access token
/// (minted by <see cref="WidgetAccessTokenService"/> at key exchange) from the
/// <c>X-Widget-Token</c> header. Unlike <see cref="ApiKeyAuthenticationHandler"/>,
/// the principal carries NO user identity — only the tenant scope. Its single
/// purpose is letting <see cref="WidgetOriginRequirement"/> and the widget chat
/// endpoint know WHICH tenant's widget is calling.
///
/// Behavior contract:
/// <list type="bullet">
///   <item>No/malformed header → <c>NoResult</c> (falls through; endpoint 401s).</item>
///   <item>Valid signature + lifetime + scope → authenticated principal with
///   <c>TenantId</c>, <c>auth_method=widget</c> and the mint-time origin claim.</item>
/// </list>
/// </summary>
public sealed class WidgetTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "WidgetToken";
    public const string HeaderName = "X-Widget-Token";
    public const string AuthMethodClaimType = "auth_method";
    public const string AuthMethodClaimValue = "widget";

    public WidgetTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var presented) || string.IsNullOrWhiteSpace(presented))
            return Task.FromResult(AuthenticateResult.NoResult());

        var token = presented.ToString();
        // Cap before validating: abusive payloads never reach the crypto path.
        if (token.Length > 4096)
            return Task.FromResult(AuthenticateResult.Fail("Invalid widget token."));

        var tokenService = Context.RequestServices.GetRequiredService<WidgetAccessTokenService>();
        var principal = tokenService.Validate(token);
        if (principal is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid widget token."));

        // Mark HOW the caller authenticated so endpoints can distinguish a
        // widget principal from a user JWT/API-key principal.
        if (principal.Identity is ClaimsIdentity identity)
            identity.AddClaim(new Claim(AuthMethodClaimType, AuthMethodClaimValue));

        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
