using System.Security.Cryptography;

namespace LmKitOmniApi.Infrastructure.Security;

/// <summary>
/// Verifies a login password in the same amount of work whether or not the account exists.
///
/// <para><b>The channel this closes.</b> Making two 401s byte-identical is only half of an
/// enumeration fix. If the endpoint short-circuits before hashing when the email is unknown but
/// runs BCrypt when it is known, the two replies are still trivially distinguishable by how
/// long they take: BCrypt at the work factor this codebase uses costs on the order of a hundred
/// milliseconds, which is several orders of magnitude above the noise of an indexed row lookup.
/// An attacker with a stopwatch enumerates accounts just as well as one reading bodies.</para>
///
/// <para><b>How.</b> When there is no account — or the stored hash is not a BCrypt hash this
/// build can verify — the presented password is verified against a decoy hash instead, so the
/// expensive path runs exactly once on every call. The decoy is minted once per process from
/// 256 bits of CSPRNG output that is then discarded: no code, no configuration and no operator
/// holds its preimage, so <see cref="Verify"/> against it cannot return <see langword="true"/>,
/// and the extra <c>isKnownHash</c> conjunction below means it could not matter if it did.</para>
///
/// <para>The decoy is minted at the library's default work factor, which is what every
/// <c>BCrypt.HashPassword</c> call site in this solution uses, so the decoy and a real hash cost
/// the same. If password hashing ever moves to an explicit work factor, this must move with
/// it.</para>
/// </summary>
public static class LoginPasswordVerifier
{
    private static readonly Lazy<string> DecoyHash = new(
        () => BCrypt.Net.BCrypt.HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// <see langword="true"/> only when <paramref name="storedHash"/> is a BCrypt hash this
    /// build understands AND <paramref name="password"/> matches it. A <see langword="null"/>
    /// or unrecognised hash costs the same and always answers <see langword="false"/>.
    /// </summary>
    public static bool Verify(string password, string? storedHash)
    {
        var isKnownHash = IsSupportedHash(storedHash);
        var hashToVerify = isKnownHash ? storedHash! : DecoyHash.Value;

        bool matches;
        try
        {
            matches = BCrypt.Net.BCrypt.Verify(password, hashToVerify);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            // A stored hash that passed the prefix check but is malformed. Do not let the
            // shape of a corrupt row become a signal either.
            matches = false;
        }

        return matches && isKnownHash;
    }

    private static bool IsSupportedHash(string? hash) =>
        hash is not null
        && (hash.StartsWith("$2a$", StringComparison.Ordinal)
            || hash.StartsWith("$2b$", StringComparison.Ordinal)
            || hash.StartsWith("$2y$", StringComparison.Ordinal));
}
