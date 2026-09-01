using System.Net;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.Security;

public sealed record DbEgressResult(bool IsAllowed, string? Reason)
{
    public static DbEgressResult Allow() => new(true, null);
    public static DbEgressResult Deny(string reason) => new(false, reason);
}

/// <summary>
/// SSRF guard for outbound DATABASE connections. The app's existing SSRF defenses
/// are HTTP-only (<see cref="ToolSandboxService.ValidateUrlAsync"/>); raw DB TCP
/// needs its own check. This resolves the target host and refuses if ANY resolved
/// address is internal/loopback/link-local (reusing
/// <see cref="ToolSandboxService.IsPrivateOrLocalAddress"/>), so a connection can
/// never be pointed at the metadata endpoint, RFC1918 hosts, or the app's own
/// database. An optional operator allowlist further restricts permitted hosts.
/// </summary>
public sealed class DbEgressValidator
{
    private readonly DatabaseAgentOptions _options;

    public DbEgressValidator(IOptions<DatabaseAgentOptions> options) => _options = options.Value;

    public async Task<DbEgressResult> ValidateHostAsync(string? host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
            return DbEgressResult.Deny("Thiếu host của cơ sở dữ liệu.");

        host = host.Trim();

        if (_options.AllowedHosts.Count > 0
            && !_options.AllowedHosts.Any(allowed => string.Equals(allowed, host, StringComparison.OrdinalIgnoreCase)))
        {
            return DbEgressResult.Deny($"Host '{host}' không nằm trong danh sách cho phép.");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, ct);
            }
            catch (Exception)
            {
                return DbEgressResult.Deny($"Không phân giải được host '{host}'.");
            }
        }

        if (addresses.Length == 0)
            return DbEgressResult.Deny($"Không phân giải được host '{host}'.");

        foreach (var address in addresses)
        {
            if (ToolSandboxService.IsPrivateOrLocalAddress(address))
                return DbEgressResult.Deny($"Địa chỉ nội bộ bị chặn ({address}).");
        }

        return DbEgressResult.Allow();
    }
}
