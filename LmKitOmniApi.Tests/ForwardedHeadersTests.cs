using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Host with forwarded-header handling OFF — the shipped default, and the state every
/// existing deployment is in.
/// </summary>
public sealed class ForwardedHeadersDisabledFixture : IDisposable
{
    public LmKitApiFactory Parent { get; } = new();
    public WebApplicationFactory<Program> Host { get; }
    public HttpClient Client { get; }

    public ForwardedHeadersDisabledFixture()
    {
        Parent.EnsureSeeded();
        // No ForwardedHeaders configuration at all: this must behave exactly as the
        // shipped appsettings.json does.
        Host = RateLimitTestHost.Create(Parent);
        Client = Host.CreateClient();
    }

    public void Dispose()
    {
        Client.Dispose();
        Host.Dispose();
        Parent.Dispose();
    }
}

public sealed class ForwardedHeadersDisabledTests : IClassFixture<ForwardedHeadersDisabledFixture>
{
    private readonly ForwardedHeadersDisabledFixture _fixture;

    public ForwardedHeadersDisabledTests(ForwardedHeadersDisabledFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// The IP fallback of the partitioning, end to end: two anonymous callers on
    /// different addresses get their own <c>SharePolicy</c> budgets.
    /// </summary>
    [Fact]
    public async Task AnonymousCallers_ArePartitionedByTheirClientIp()
    {
        var exhausted = await RateLimitTestHost.DrainSharePolicyAsync(_fixture.Client, "203.0.113.1");
        var otherClient = await RateLimitTestHost.AnonymousShareReadAsync(_fixture.Client, "203.0.113.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted);
        Assert.Equal(HttpStatusCode.NotFound, otherClient);
    }

    /// <summary>
    /// REGRESSION GUARD for the change in this branch. With the feature disabled the
    /// forwarded-headers middleware is not in the pipeline at all, so a caller cannot
    /// mint a fresh per-IP budget by inventing an <c>X-Forwarded-For</c> value: 31
    /// requests carrying 31 different spoofed addresses still exhaust the ONE bucket
    /// belonging to their real peer. If someone later enables forwarded headers
    /// carelessly — or drops the known-proxy gate — this test fails.
    /// </summary>
    [Fact]
    public async Task SpoofedForwardedFor_IsIgnored_WhenTheFeatureIsDisabled()
    {
        var exhausted = await RateLimitTestHost.DrainSharePolicyAsync(
            _fixture.Client,
            peerIp: "203.0.113.3",
            forwardedForFactory: attempt => $"198.51.100.{attempt + 1}");

        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted);
    }
}

/// <summary>
/// Host with forwarded-header handling ON and exactly one trusted proxy.
/// </summary>
public sealed class ForwardedHeadersEnabledFixture : IDisposable
{
    public const string TrustedProxyIp = "203.0.113.50";
    public const string UntrustedPeerIp = "198.51.100.77";

    public LmKitApiFactory Parent { get; } = new();
    public WebApplicationFactory<Program> Host { get; }
    public HttpClient Client { get; }

    public ForwardedHeadersEnabledFixture()
    {
        Parent.EnsureSeeded();
        Host = RateLimitTestHost.Create(Parent, new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownProxies:0"] = TrustedProxyIp,
            ["ForwardedHeaders:ForwardLimit"] = "1"
        });
        Client = Host.CreateClient();
    }

    public void Dispose()
    {
        Client.Dispose();
        Host.Dispose();
        Parent.Dispose();
    }
}

public sealed class ForwardedHeadersEnabledTests : IClassFixture<ForwardedHeadersEnabledFixture>
{
    private readonly ForwardedHeadersEnabledFixture _fixture;

    public ForwardedHeadersEnabledTests(ForwardedHeadersEnabledFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// THE BUG THIS FIXES. Behind nginx every caller shared one per-IP bucket because
    /// the peer address was always nginx's. With the proxy trusted, two clients behind
    /// it are now limited independently.
    /// </summary>
    [Fact]
    public async Task ForwardedFor_BecomesTheClientIp_WhenThePeerIsATrustedProxy()
    {
        var exhausted = await RateLimitTestHost.DrainSharePolicyAsync(
            _fixture.Client,
            peerIp: ForwardedHeadersEnabledFixture.TrustedProxyIp,
            forwardedForFactory: _ => "198.51.100.10");

        var otherClientBehindTheSameProxy = await RateLimitTestHost.AnonymousShareReadAsync(
            _fixture.Client,
            peerIp: ForwardedHeadersEnabledFixture.TrustedProxyIp,
            forwardedFor: "198.51.100.11");

        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted);
        Assert.Equal(HttpStatusCode.NotFound, otherClientBehindTheSameProxy);
    }

    /// <summary>
    /// The known-proxy gate itself: the SAME enabled host must still ignore
    /// <c>X-Forwarded-For</c> from a peer that is not on the trust list, otherwise
    /// anyone reaching Kestrel directly could mint unlimited buckets. 31 spoofed
    /// addresses from an untrusted peer still exhaust that peer's single bucket.
    /// </summary>
    [Fact]
    public async Task ForwardedFor_IsIgnored_WhenThePeerIsNotATrustedProxy()
    {
        var exhausted = await RateLimitTestHost.DrainSharePolicyAsync(
            _fixture.Client,
            peerIp: ForwardedHeadersEnabledFixture.UntrustedPeerIp,
            forwardedForFactory: attempt => $"198.51.100.{100 + attempt}");

        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted);
    }
}

public sealed class ForwardedHeadersStartupTests
{
    /// <summary>
    /// Enabling the feature without naming a trusted peer is a misconfiguration that
    /// would either do nothing or — if the framework's loopback defaults were left in
    /// place — trust the wrong peer. It must stop the process at startup, not degrade
    /// silently on the first request.
    /// </summary>
    [Fact]
    public void EnablingWithoutAnyTrustedProxy_FailsAtStartup()
    {
        using var parent = new LmKitApiFactory();
        using var host = RateLimitTestHost.Create(parent, new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true"
        });

        var failure = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        var description = RateLimitTestHost.Describe(failure);
        Assert.Contains("ForwardedHeaders:KnownProxies", description, StringComparison.Ordinal);
        Assert.Contains("ForwardedHeaders:KnownNetworks", description, StringComparison.Ordinal);
    }

    /// <summary>A /0 network is "trust the whole internet" — refused for the same reason.</summary>
    [Fact]
    public void TrustingEveryAddress_FailsAtStartup()
    {
        using var parent = new LmKitApiFactory();
        using var host = RateLimitTestHost.Create(parent, new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownNetworks:0"] = "0.0.0.0/0"
        });

        var failure = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains("trusts EVERY address", RateLimitTestHost.Describe(failure), StringComparison.Ordinal);
    }

    /// <summary>A typo in the trust list must fail loudly rather than be skipped.</summary>
    [Fact]
    public void AMalformedTrustList_FailsAtStartup()
    {
        using var parent = new LmKitApiFactory();
        using var host = RateLimitTestHost.Create(parent, new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownProxies:0"] = "not-an-ip"
        });

        var failure = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains("not a valid IP address", RateLimitTestHost.Describe(failure), StringComparison.Ordinal);
    }
}
