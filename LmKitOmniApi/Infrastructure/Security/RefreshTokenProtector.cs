using System.Security.Cryptography;
using System.Text;

namespace LmKitOmniApi.Infrastructure.Security;

/// <summary>
/// Mints and hashes refresh tokens, and binds each one to the token FAMILY it belongs to.
///
/// <para><b>Why a family.</b> Rotation replaces <c>UserSession.RefreshTokenHash</c> in place, so
/// the moment a token is rotated its hash is gone and the superseded value becomes
/// indistinguishable from a random string: the endpoint could reject a replay but could not
/// tell WHOSE session had been replayed, and therefore could not revoke it (OAuth 2.0 Security
/// BCP §4.14.2). Carrying the family in the token itself closes that without a schema change —
/// the family is the <see cref="Domain.Entities.UserSession"/> row, whose primary key is
/// already stable across every rotation of that login.</para>
///
/// <para><b>Format.</b> <c>{familyId:N}.{secret}</c> — 32 hex characters, a dot, then the same
/// 32 random bytes in standard Base64 the previous format used. The separator is unambiguous:
/// the Base64 alphabet is <c>A-Z a-z 0-9 + / =</c> and the family is hex, so a <c>.</c> at
/// index 32 can only be the separator. A token WITHOUT one is a pre-upgrade cookie and is still
/// accepted by hash lookup, which is why <see cref="Generate"/> keeps its old contract.</para>
///
/// <para><b>The family id is not a secret and is not treated as one.</b> It names a row; it
/// authenticates nothing. Authentication is still the 256-bit secret, and what is stored is
/// still only a SHA-256 of the whole token, so a database reader cannot mint a usable
/// cookie.</para>
/// </summary>
public static class RefreshTokenProtector
{
    /// <summary>Separates the family id from the secret. Absent from both alphabets around it.</summary>
    private const char FamilySeparator = '.';

    /// <summary>A GUID in "N" format is exactly this many hex characters.</summary>
    private const int FamilyIdLength = 32;

    /// <summary>The unbound secret half: 256 bits of CSPRNG output, Base64.</summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>A fresh secret bound to <paramref name="familyId"/>.</summary>
    public static string GenerateFor(Guid familyId) =>
        string.Concat(familyId.ToString("N"), FamilySeparator, Generate());

    /// <summary>
    /// Reads the family a presented token claims to belong to. <see langword="false"/> for a
    /// legacy token, a fabricated one, or anything else that is not exactly
    /// <c>{32 hex}.{something}</c> — callers must fall back to a hash lookup, and must never
    /// treat a parsed family as evidence the token is valid.
    /// </summary>
    public static bool TryReadFamily(string? token, out Guid familyId)
    {
        familyId = Guid.Empty;
        if (string.IsNullOrEmpty(token) || token.Length <= FamilyIdLength) return false;
        if (token[FamilyIdLength] != FamilySeparator) return false;

        return Guid.TryParseExact(token.AsSpan(0, FamilyIdLength), "N", out familyId);
    }

    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
