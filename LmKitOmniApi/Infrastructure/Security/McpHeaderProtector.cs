using Microsoft.AspNetCore.DataProtection;

namespace LmKitOmniApi.Infrastructure.Security;

public sealed class McpHeaderProtector
{
    private const string Prefix = "dp:v1:";
    private readonly IDataProtector _protector;

    public McpHeaderProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("LmKitOmniApi.McpHeaders.v1");

    public string Protect(string value) => Prefix + _protector.Protect(value);

    public string Unprotect(string value) => value.StartsWith(Prefix, StringComparison.Ordinal)
        ? _protector.Unprotect(value[Prefix.Length..])
        : value;
}
