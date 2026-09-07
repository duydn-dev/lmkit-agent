using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
// Both namespaces define IPNetwork and the two are unrelated types. CIDR text is parsed
// with the BCL one (it is the only one with TryParse) and then converted to the
// ASP.NET Core one, which is what ForwardedHeadersOptions.KnownNetworks holds.
using ProxyNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;
using CidrNetwork = System.Net.IPNetwork;

namespace LmKitOmniApi.Infrastructure.Security;

/// <summary>
/// Opt-in, explicitly-trusted <c>X-Forwarded-For</c> handling.
///
/// <para><b>The bug this fixes.</b> Behind the nginx container every request reaches
/// Kestrel from nginx's address, so <c>Connection.RemoteIpAddress</c> was the SAME
/// value for every caller on the planet. Everything that keys on it degraded
/// accordingly: the per-IP rate limits (login, share links, widget key exchange, and
/// the anonymous fallback of <see cref="RateLimitPartitionKey"/>) collapsed into ONE
/// global bucket, and the audit trail plus the failed-login record all recorded the
/// proxy instead of the client.</para>
///
/// <para><b>Why it is off by default.</b> Trusting <c>X-Forwarded-For</c> from an
/// arbitrary peer is strictly worse than the bug: any client could then choose its
/// own apparent address, mint an unlimited number of per-IP rate-limit buckets, and
/// write attacker-chosen addresses into the audit log. So the feature is gated on
/// <c>ForwardedHeaders:Enabled</c> (default <c>false</c>) AND on an explicit list of
/// trusted peers. When it is disabled the middleware is not added to the pipeline at
/// all — a forwarded header is then not merely untrusted, it is never read.</para>
///
/// <para><b>Fail loud, not open.</b> The framework ships non-empty
/// <c>KnownProxies</c>/<c>KnownNetworks</c> defaults (IPv6 loopback). Those defaults
/// are CLEARED here for two reasons: a deployment must state its own trust boundary
/// rather than inherit one, and leaving them in place would make "the operator
/// configured no proxies" undetectable. Because they are cleared, enabling the
/// feature without naming a single proxy or network would trust NOTHING and silently
/// do nothing at all, so that combination throws at startup instead. Operators that
/// really do terminate on loopback must list <c>127.0.0.1</c> / <c>::1</c>
/// themselves.</para>
///
/// <para>Configuration (see <c>appsettings.json</c>):</para>
/// <code>
/// "ForwardedHeaders": {
///   "Enabled": false,
///   "KnownProxies": [],      // e.g. [ "172.18.0.2" ]  — the nginx container address
///   "KnownNetworks": [],     // e.g. [ "172.18.0.0/16" ] — the docker bridge subnet
///   "ForwardLimit": 1,
///   "IncludeProto": false
/// }
/// </code>
/// </summary>
public static class ForwardedHeadersSetup
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>Default hops to unwind. 1 = exactly one trusted reverse proxy.</summary>
    private const int DefaultForwardLimit = 1;

    /// <summary>
    /// Validates the configuration and, when enabled, registers the
    /// <see cref="ForwardedHeadersOptions"/> the pipeline will use. Validation is
    /// eager (here, not inside the options callback) so a misconfigured deployment
    /// fails at startup instead of on the first request.
    /// </summary>
    public static IServiceCollection AddConfiguredForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!IsEnabled(configuration))
            return services;

        var section = configuration.GetSection(SectionName);
        var proxies = ParseProxies(ReadList(section, "KnownProxies"));
        var networks = ParseNetworks(ReadList(section, "KnownNetworks"));

        if (proxies.Count == 0 && networks.Count == 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:Enabled is true but neither {SectionName}:KnownProxies nor "
                + $"{SectionName}:KnownNetworks lists a trusted peer. Forwarded headers would "
                + "either be ignored entirely or — worse, if the defaults were kept — accepted "
                + "from an untrusted peer, letting any client spoof its client IP. List the "
                + "reverse proxy's address (or its subnet), or set "
                + $"{SectionName}:Enabled to false.");
        }

        var forwardLimit = section.GetValue("ForwardLimit", DefaultForwardLimit);
        if (forwardLimit < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:ForwardLimit must be at least 1 (got {forwardLimit}). "
                + "An unlimited chain would let a client prepend its own hops.");
        }

        // X-Forwarded-Proto is opt-in: it rewrites Request.Scheme, which changes
        // HTTPS redirection/HSTS behaviour. Turning it on is correct behind a
        // TLS-terminating proxy, but it is a separate decision from "who is the
        // client", so it is not implied by enabling this feature.
        var includeProto = section.GetValue("IncludeProto", false);

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = includeProto
                ? Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                  | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                : Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor;
            options.ForwardLimit = forwardLimit;

            // Inherit no trust: see the type remarks.
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            foreach (var proxy in proxies) options.KnownProxies.Add(proxy);
            foreach (var network in networks) options.KnownNetworks.Add(network);
        });

        return services;
    }

    /// <summary>
    /// Adds <c>UseForwardedHeaders</c> — and ONLY when the feature is enabled, so a
    /// disabled deployment cannot be talked into reading the header at all.
    /// Must be called before any middleware that reads the client IP: authentication
    /// (failed-login records), the rate limiter, and the audit interceptor.
    /// </summary>
    public static IApplicationBuilder UseConfiguredForwardedHeaders(
        this IApplicationBuilder app,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(configuration);

        var logger = app.ApplicationServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(ForwardedHeadersSetup).FullName!);

        if (!IsEnabled(configuration))
        {
            logger.LogInformation(
                "{Section}:Enabled is false; X-Forwarded-For is ignored and the rate limiter, "
                + "audit log and login records use the direct peer address.",
                SectionName);
            return app;
        }

        var section = configuration.GetSection(SectionName);
        logger.LogInformation(
            "Trusting forwarded headers from proxies [{Proxies}] and networks [{Networks}] "
            + "with ForwardLimit {ForwardLimit}.",
            string.Join(", ", ReadList(section, "KnownProxies")),
            string.Join(", ", ReadList(section, "KnownNetworks")),
            section.GetValue("ForwardLimit", DefaultForwardLimit));

        return app.UseForwardedHeaders();
    }

    internal static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue($"{SectionName}:Enabled", false);

    private static string[] ReadList(IConfiguration section, string key) =>
        section.GetSection(key).Get<string[]>() ?? [];

    private static List<IPAddress> ParseProxies(IReadOnlyList<string> values)
    {
        var parsed = new List<IPAddress>(values.Count);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!IPAddress.TryParse(value.Trim(), out var address))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:KnownProxies contains '{value}', which is not a valid IP address.");
            }
            parsed.Add(address);
        }
        return parsed;
    }

    private static List<ProxyNetwork> ParseNetworks(IReadOnlyList<string> values)
    {
        var parsed = new List<ProxyNetwork>(values.Count);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var trimmed = value.Trim();
            if (!CidrNetwork.TryParse(trimmed, out var network))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:KnownNetworks contains '{value}', which is not valid CIDR "
                    + "notation (expected e.g. \"172.18.0.0/16\").");
            }

            // A /0 is "trust every address on the internet", which is precisely the
            // spoofing hole this gate exists to prevent. Refuse it rather than let a
            // copy-pasted value quietly re-open it.
            if (network.PrefixLength == 0)
            {
                throw new InvalidOperationException(
                    $"{SectionName}:KnownNetworks contains '{value}', which trusts EVERY address "
                    + "and would let any client spoof its client IP. List the proxy's actual subnet.");
            }
            parsed.Add(new ProxyNetwork(network.BaseAddress, network.PrefixLength));
        }
        return parsed;
    }
}
