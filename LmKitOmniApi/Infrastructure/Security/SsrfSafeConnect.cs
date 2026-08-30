using System.Net;
using System.Net.Sockets;
using LmKitOmniApi.Infrastructure.AI.Security;

namespace LmKitOmniApi.Infrastructure.Security;

/// <summary>
/// Shared factory for a <see cref="SocketsHttpHandler.ConnectCallback"/> that closes
/// the DNS-rebinding TOCTOU window used by SSRF attacks.
///
/// Any pre-invocation URL/DNS check (the MCP sandbox, the research URL validator)
/// resolves DNS once; a plain handler then resolves it AGAIN for the actual request,
/// so a malicious resolver can answer with a public IP during validation and rebind
/// to 169.254.169.254 / RFC1918 space for the connection. This callback re-resolves,
/// re-vets EVERY resolved address with the authoritative
/// <see cref="ToolSandboxService.IsPrivateOrLocalAddress"/> classifier, and opens a
/// socket only to a vetted PUBLIC IP. TLS still validates against the original
/// hostname via SNI. When nothing resolves to an allowed public address, or no
/// vetted address accepts a connection, an <see cref="HttpRequestException"/> is
/// thrown so callers treat the fetch as a failed request.
/// </summary>
public static class SsrfSafeConnect
{
    /// <summary>
    /// Builds a fresh vetted <see cref="SocketsHttpHandler.ConnectCallback"/>
    /// delegate. The delegate is stateless, so a single instance could be shared,
    /// but a factory keeps each handler independent.
    /// </summary>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> CreateVettedConnectCallback()
    {
        return static async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host, ct);
            var vetted = addresses
                .Where(address => !ToolSandboxService.IsPrivateOrLocalAddress(address))
                .ToArray();
            if (vetted.Length == 0)
                throw new HttpRequestException(
                    $"Host '{host}' does not resolve to any allowed public address.");

            Exception? lastFailure = null;
            foreach (var address in vetted)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex)
                {
                    socket.Dispose();
                    lastFailure = ex;
                }
            }

            throw new HttpRequestException(
                $"Unable to connect to host '{host}' on any vetted public address.", lastFailure);
        };
    }
}
