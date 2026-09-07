using System.Net;
using System.Security.Claims;

namespace LmKitOmniApi.Infrastructure.Security;

/// <summary>
/// THE single derivation of a rate-limit partition key. Both the in-process ASP.NET
/// limiter policies (<c>Program.cs</c>) and the cross-replica Redis window
/// (<see cref="DistributedAiRateLimitMiddleware"/>) call this, so one logical budget
/// can never be split across two different key shapes again — before this type the
/// policy keyed on <c>User.Identity.Name</c> while the Redis middleware keyed on the
/// <see cref="ClaimTypes.NameIdentifier"/> claim, which are the same value today only
/// by accident of the JWT options' <c>NameClaimType</c> mapping.
///
/// <para><b>Precedence</b> (the product requirement: API key first, then the JWT
/// identity, and IP only as a last resort):</para>
/// <list type="number">
///   <item><c>apikey:{keyId}</c> — the caller authenticated with <c>X-Api-Key</c>.
///   The partition is the KEY, not its owner, so two keys minted by the same user
///   get two independent budgets (that is exactly what "rate-limit theo apiKey"
///   asks for, and it is also what lets an operator revoke one noisy integration
///   without throttling the others). The value is the key's surrogate
///   <c>TenantApiKey.Id</c> — never the raw key and never its hash.</item>
///   <item><c>user:{tenantId}:{userId}</c> — any other authenticated principal
///   (the JWT cookie/bearer flow, and the widget token whose subject is
///   <c>widget:{tenantId}</c>). Tenant-scoped so a user id that somehow repeated
///   across tenants still cannot share a bucket.</item>
///   <item><c>ip:{address}</c> — anonymous callers.</item>
///   <item><c>anon:global</c> — anonymous AND no remote IP (the server did not
///   surface one). Deliberately a single shared bucket: an unattributable caller
///   must not get a free budget.</item>
/// </list>
///
/// <para>Every branch carries an explicit, non-overlapping prefix, so a partition
/// derived from caller-controlled-ish data (an IP) can never collide with one
/// derived from a server-issued identity (a user id or a key id) — without the
/// prefixes an attacker who could pick their address representation could try to
/// land in another tenant's bucket.</para>
///
/// <para>This runs on EVERY request that hits a limited endpoint, so it allocates at
/// most one string and does no LINQ/regex.</para>
/// </summary>
public static class RateLimitPartitionKey
{
    /// <summary>Bucket for callers we cannot attribute at all. Shared on purpose.</summary>
    public const string AnonymousPartition = "anon:global";

    private const string ApiKeyPrefix = "apikey:";
    private const string UserPrefix = "user:";
    private const string IpPrefix = "ip:";

    /// <summary>Tenant claim minted by both the JWT flow and the API-key handler.</summary>
    private const string TenantIdClaimType = "TenantId";

    /// <summary>Placeholder when an authenticated principal carries no tenant claim.</summary>
    private const string UnknownTenant = "-";

    /// <summary>
    /// Full precedence: API key → JWT/other authenticated subject → client IP →
    /// shared anonymous bucket. Use this for limits on AUTHENTICATED surfaces.
    /// </summary>
    public static string Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            // 1) API key: the key itself is the budget holder.
            var apiKeyId = user.FindFirstValue(ApiKeyAuthenticationHandler.ApiKeyIdClaimType);
            if (!string.IsNullOrEmpty(apiKeyId))
                return string.Concat(ApiKeyPrefix, apiKeyId);

            // 2) Any other authenticated principal: tenant-scoped subject.
            var subject = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrEmpty(subject))
            {
                var tenant = user.FindFirstValue(TenantIdClaimType);
                return string.Concat(
                    UserPrefix,
                    string.IsNullOrEmpty(tenant) ? UnknownTenant : tenant,
                    ":",
                    subject);
            }
        }

        // 3) / 4) Fall back to the transport identity.
        return ResolveClientIp(context);
    }

    /// <summary>
    /// IP-ONLY partition, for limits that guard UNAUTHENTICATED endpoints
    /// (login, share-link reads, the widget key exchange). Those must NOT use
    /// <see cref="Resolve"/>: the caller supplies the credential being brute-forced,
    /// so partitioning by any identity derived from it would let an attacker mint a
    /// fresh bucket per guess and defeat the limit entirely. Shared with
    /// <see cref="Resolve"/> only so both normalise the address the same way.
    /// </summary>
    public static string ResolveClientIp(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var address = context.Connection.RemoteIpAddress;
        if (address is null)
            return AnonymousPartition;

        // "::ffff:203.0.113.7" and "203.0.113.7" are the same host; without this they
        // would be two buckets, i.e. double the budget for anyone who can pick the
        // representation their proxy emits.
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return string.Concat(IpPrefix, address.ToString());
    }

    /// <summary>
    /// True when <paramref name="partition"/> came from the IP fallback rather than
    /// from an authenticated identity. Exposed for tests and diagnostics.
    /// </summary>
    public static bool IsIpPartition(string partition) =>
        partition is not null && partition.StartsWith(IpPrefix, StringComparison.Ordinal);
}
