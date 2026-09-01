using Microsoft.AspNetCore.DataProtection;

namespace LmKitOmniApi.Infrastructure.Security;

/// <summary>
/// Encrypts/decrypts external database connection strings at rest, mirroring
/// <see cref="McpHeaderProtector"/> with its own purpose string. A connection
/// string is a REVERSIBLE secret (unlike a hashed API key), so it must be
/// decryptable — which makes the DataProtection key ring's protection the last
/// line of defense. Operators MUST enable a cert/KMS-wrapped key ring in
/// production (DataProtection:CertificatePath); otherwise keys — and therefore
/// every tenant's DB credentials — sit in plaintext on disk.
/// </summary>
public sealed class DbConnectionSecretProtector
{
    private const string Prefix = "dp:v1:";
    private readonly IDataProtector _protector;

    public DbConnectionSecretProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("LmKitOmniApi.DbConnection.v1");

    public string Protect(string value) => Prefix + _protector.Protect(value);

    public string Unprotect(string value) => value.StartsWith(Prefix, StringComparison.Ordinal)
        ? _protector.Unprotect(value[Prefix.Length..])
        : value;
}
