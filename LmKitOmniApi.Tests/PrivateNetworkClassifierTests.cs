using System.Net;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.Security;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Table-driven coverage for the ONE authoritative SSRF address classifier
/// (<see cref="PrivateNetworkClassifier"/>) that now backs every outbound-egress gate:
/// the vetted socket ConnectCallback, tool-sandbox URL validation, external-database TCP,
/// and remote model downloads.
///
/// The regressions this locks down:
///  - IPv6 ULA fc00::/7 (fd00::/8 is what Docker IPv6 networks hand out) used to be ALLOWED,
///    because IsIPv6SiteLocal only knows the deprecated fec0::/10 range.
///  - The unspecified address :: used to be ALLOWED on the shared path, and connecting to ::
///    reaches the local host.
///  - IPv4-mapped IPv6 (::ffff:169.254.169.254) was classified by address family alone, so
///    the embedded IPv4 was never examined.
///  - CGNAT, 192.0.0.0/24, 198.18.0.0/15, multicast and 255.255.255.255 were documented as
///    blocked in WebReadOptions but were not blocked here.
/// </summary>
public sealed class PrivateNetworkClassifierTests
{
    [Theory]
    // ── IPv4: blocked ranges ──
    [InlineData("0.0.0.0", true)]                 // 0.0.0.0/8 — reaches the local host
    [InlineData("0.1.2.3", true)]
    [InlineData("10.0.0.1", true)]                // 10/8
    [InlineData("10.255.255.255", true)]
    [InlineData("100.64.0.1", true)]              // 100.64/10 CGNAT
    [InlineData("100.127.255.255", true)]
    [InlineData("127.0.0.1", true)]               // loopback
    [InlineData("127.255.255.254", true)]
    [InlineData("169.254.169.254", true)]         // cloud metadata
    [InlineData("169.254.0.1", true)]
    [InlineData("172.16.0.1", true)]              // 172.16/12 (Docker bridge pool)
    [InlineData("172.17.0.2", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.0.0.170", true)]             // 192.0.0.0/24 IETF protocol assignments
    [InlineData("192.168.1.1", true)]             // 192.168/16
    [InlineData("198.18.0.1", true)]              // 198.18/15 benchmarking
    [InlineData("198.19.255.254", true)]
    [InlineData("224.0.0.1", true)]               // 224/4 multicast
    [InlineData("239.255.255.250", true)]
    [InlineData("240.0.0.1", true)]               // 240/4 reserved
    [InlineData("255.255.255.255", true)]         // limited broadcast
    // ── IPv4: public addresses stay reachable ──
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("100.63.255.255", false)]         // just below CGNAT
    [InlineData("100.128.0.1", false)]            // just above CGNAT
    [InlineData("172.15.255.255", false)]         // just below 172.16/12
    [InlineData("172.32.0.1", false)]             // just above 172.16/12
    [InlineData("192.0.1.1", false)]              // just above 192.0.0.0/24
    [InlineData("198.17.255.255", false)]         // just below 198.18/15
    [InlineData("198.20.0.1", false)]             // just above 198.18/15
    [InlineData("223.255.255.255", false)]        // just below multicast
    [InlineData("169.253.255.255", false)]
    public void Ipv4Ranges_AreClassifiedExactly(string address, bool expectedPrivate) =>
        Assert.Equal(expectedPrivate, PrivateNetworkClassifier.IsPrivateOrLocal(IPAddress.Parse(address)));

    [Theory]
    // ── IPv6: blocked ──
    [InlineData("::", true)]                               // unspecified — reaches the local host
    [InlineData("::1", true)]                              // loopback
    [InlineData("fd00::1", true)]                          // ULA — Docker IPv6 networks
    [InlineData("fd12:3456:789a::1", true)]
    [InlineData("fc00::1", true)]                          // ULA lower half of fc00::/7
    [InlineData("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)]
    [InlineData("fe80::1", true)]                          // link-local
    [InlineData("fec0::1", true)]                          // deprecated site-local
    [InlineData("ff02::1", true)]                          // multicast
    [InlineData("::ffff:127.0.0.1", true)]                 // IPv4-mapped loopback
    [InlineData("::ffff:169.254.169.254", true)]           // IPv4-mapped metadata endpoint
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("::ffff:192.168.1.1", true)]
    [InlineData("::127.0.0.1", true)]                      // deprecated IPv4-compatible form
    [InlineData("64:ff9b::169.254.169.254", true)]         // NAT64 to the metadata endpoint
    [InlineData("64:ff9b::10.0.0.1", true)]
    [InlineData("2002:c0a8:0101::", true)]                 // 6to4 wrapping 192.168.1.1
    // ── IPv6: public stays reachable ──
    [InlineData("2001:4860:4860::8888", false)]            // Google public DNS
    [InlineData("2606:4700:4700::1111", false)]            // Cloudflare
    [InlineData("::ffff:8.8.8.8", false)]                  // IPv4-mapped public address
    [InlineData("64:ff9b::8.8.8.8", false)]                // NAT64 to a public address
    [InlineData("2002:0808:0808::", false)]                // 6to4 wrapping 8.8.8.8
    [InlineData("fb00::1", false)]                         // just below fc00::/7
    [InlineData("fe00::1", false)]                         // between ULA and link-local
    public void Ipv6Ranges_AreClassifiedExactly(string address, bool expectedPrivate) =>
        Assert.Equal(expectedPrivate, PrivateNetworkClassifier.IsPrivateOrLocal(IPAddress.Parse(address)));

    /// <summary>
    /// The two former copies of this logic disagreed with each other and with the shared
    /// gate. Both entry points must now answer identically for every case in the table.
    /// </summary>
    [Theory]
    [InlineData("fd00::1")]
    [InlineData("::")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("8.8.8.8")]
    [InlineData("2001:4860:4860::8888")]
    public void BothLegacyEntryPoints_DelegateToTheSameClassifier(string address)
    {
        var parsed = IPAddress.Parse(address);
        var expected = PrivateNetworkClassifier.IsPrivateOrLocal(parsed);

        Assert.Equal(expected, ToolSandboxService.IsPrivateOrLocalAddress(parsed));
        Assert.Equal(expected, LmModelManager.IsPrivateOrLocalAddress(parsed));
    }

    /// <summary>Literal-IP URLs in the newly covered ranges must be refused by the tool sandbox.</summary>
    [Theory]
    [InlineData("http://[fd00::1]/internal")]
    [InlineData("http://[::]/internal")]
    [InlineData("http://[::ffff:169.254.169.254]/latest/meta-data/")]
    [InlineData("http://100.64.0.1/internal")]
    [InlineData("http://198.18.0.1/internal")]
    public void ToolSandbox_RefusesLiteralUrlsInTheNewlyCoveredRanges(string url)
    {
        var sandbox = new ToolSandboxService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolSandboxService>.Instance);

        Assert.False(sandbox.ValidateUrl(url).IsAllowed);
    }

    [Fact]
    public void PublicLiteralUrl_IsStillAllowed()
    {
        var sandbox = new ToolSandboxService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolSandboxService>.Instance);

        Assert.True(sandbox.ValidateUrl("https://8.8.8.8/health").IsAllowed);
    }
}
