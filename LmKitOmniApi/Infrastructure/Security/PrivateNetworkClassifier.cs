using System.Net;
using System.Net.Sockets;

namespace LmKitOmniApi.Infrastructure.Security;

/// <summary>
/// THE single authoritative "is this address inside our own network?" classifier.
///
/// Every outbound-egress gate in the app funnels through here:
/// <list type="bullet">
///   <item><see cref="SsrfSafeConnect"/> — the socket <c>ConnectCallback</c> on the MCP /
///   research / web-read <c>SocketsHttpHandler</c>s (closes the DNS-rebinding TOCTOU window).</item>
///   <item><c>ToolSandboxService.ValidateUrl(Async)</c> — pre-flight URL + DNS vetting.</item>
///   <item><c>DbEgressValidator</c> — raw TCP to external databases.</item>
///   <item><c>LmModelManager</c> — remote model downloads.</item>
/// </list>
///
/// It used to be TWO divergent copies (one in <c>ToolSandboxService</c>, one in
/// <c>LmModelManager</c>) and the shared one was the weaker of the two. Both now delegate
/// here so a range can never again be blocked on one path and open on another.
///
/// Deny list (an address matching any entry is refused):
/// <code>
/// IPv4   0.0.0.0/8          "this network" — 0.0.0.0 reaches the local host
///        10.0.0.0/8         RFC1918
///        100.64.0.0/10      RFC6598 CGNAT (carrier / cloud-internal)
///        127.0.0.0/8        loopback
///        169.254.0.0/16     link-local, incl. the 169.254.169.254 cloud metadata endpoint
///        172.16.0.0/12      RFC1918 (Docker's default bridge pool)
///        192.0.0.0/24       IETF protocol assignments (incl. 192.0.0.170 NAT64 discovery)
///        192.168.0.0/16     RFC1918
///        198.18.0.0/15      RFC2544 benchmarking
///        224.0.0.0/4        multicast
///        240.0.0.0/4        reserved, incl. 255.255.255.255 limited broadcast
/// IPv6   ::/96              unspecified (::), loopback (::1) and the deprecated
///                           IPv4-compatible form — classified by the embedded IPv4
///        ::ffff:0:0/96      IPv4-mapped — classified by the embedded IPv4
///        64:ff9b::/96       NAT64 well-known prefix — classified by the embedded IPv4
///        2002::/16          6to4 — classified by the embedded IPv4 tunnel endpoint
///        fc00::/7           unique local (fd00::/8 is what Docker IPv6 networks hand out)
///        fe80::/10          link-local
///        fec0::/10          deprecated site-local
///        ff00::/8           multicast
/// </code>
///
/// Anything that is neither IPv4 nor IPv6 cannot be vetted, so it is refused (fail closed).
/// </summary>
public static class PrivateNetworkClassifier
{
    /// <summary>
    /// True when <paramref name="address"/> is loopback, private, link-local, CGNAT,
    /// multicast, reserved, or otherwise not a routable public destination.
    /// </summary>
    public static bool IsPrivateOrLocal(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPrivateOrLocalIPv4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPrivateOrLocalIPv6(address),
            _ => true // Unknown family: fail closed rather than opening a hole.
        };
    }

    private static bool IsPrivateOrLocalIPv4(ReadOnlySpan<byte> octets) =>
        octets[0] == 0                                          // 0.0.0.0/8
        || octets[0] == 10                                      // 10.0.0.0/8
        || (octets[0] == 100 && octets[1] is >= 64 and <= 127)  // 100.64.0.0/10 CGNAT
        || octets[0] == 127                                     // 127.0.0.0/8
        || (octets[0] == 169 && octets[1] == 254)               // 169.254.0.0/16 (metadata)
        || (octets[0] == 172 && octets[1] is >= 16 and <= 31)   // 172.16.0.0/12
        || (octets[0] == 192 && octets[1] == 0 && octets[2] == 0) // 192.0.0.0/24
        || (octets[0] == 192 && octets[1] == 168)               // 192.168.0.0/16
        || (octets[0] == 198 && octets[1] is 18 or 19)          // 198.18.0.0/15
        || octets[0] >= 224;                                    // 224.0.0.0/4 + 240.0.0.0/4

    private static bool IsPrivateOrLocalIPv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        // ::ffff:a.b.c.d — an IPv4 destination wearing an IPv6 costume. Without this,
        // ::ffff:169.254.169.254 walked straight past an IPv6-family check.
        if (address.IsIPv4MappedToIPv6)
            return IsPrivateOrLocalIPv4(bytes.AsSpan(12, 4));

        // ::/96 — covers :: (which connects to the local host), ::1, and the deprecated
        // IPv4-compatible ::a.b.c.d. Classifying by the embedded IPv4 blocks :: (0.0.0.0/8)
        // and ::1 (also 0.0.0.0/8) without hand-coding either case.
        if (IsAllZero(bytes.AsSpan(0, 12)))
            return IsPrivateOrLocalIPv4(bytes.AsSpan(12, 4));

        // 64:ff9b::/96 — NAT64 well-known prefix. A NAT64 gateway would happily relay
        // 64:ff9b::169.254.169.254 to the metadata service, so vet the embedded IPv4.
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b
            && IsAllZero(bytes.AsSpan(4, 8)))
            return IsPrivateOrLocalIPv4(bytes.AsSpan(12, 4));

        // 2002::/16 — 6to4 carries the IPv4 tunnel endpoint in bytes 2..5.
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
            return IsPrivateOrLocalIPv4(bytes.AsSpan(2, 4));

        // fc00::/7 — unique local addresses. IsIPv6SiteLocal only knows the DEPRECATED
        // fec0::/10 range, so ULA (and therefore every Docker IPv6 network, which hands
        // out fd00::/8) used to be treated as public.
        if ((bytes[0] & 0xFE) == 0xFC)
            return true;

        return address.IsIPv6LinkLocal      // fe80::/10
            || address.IsIPv6SiteLocal      // fec0::/10 (deprecated)
            || address.IsIPv6Multicast;     // ff00::/8
    }

    private static bool IsAllZero(ReadOnlySpan<byte> bytes) => bytes.IndexOfAnyExcept((byte)0) < 0;
}
