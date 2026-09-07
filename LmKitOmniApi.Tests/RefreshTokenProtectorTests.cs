using LmKitOmniApi.Infrastructure.Security;

namespace LmKitOmniApi.Tests;

public class RefreshTokenProtectorTests
{
    [Fact]
    public void Generate_ReturnsUniqueHighEntropyTokens()
    {
        var first = RefreshTokenProtector.Generate();
        var second = RefreshTokenProtector.Generate();

        Assert.NotEqual(first, second);
        Assert.Equal(32, Convert.FromBase64String(first).Length);
    }

    [Fact]
    public void Hash_IsDeterministicAndDoesNotStoreRawToken()
    {
        const string token = "sample-refresh-token";

        var first = RefreshTokenProtector.Hash(token);
        var second = RefreshTokenProtector.Hash(token);

        Assert.Equal(first, second);
        Assert.NotEqual(token, first);
        Assert.Equal(64, first.Length);
    }

    /// <summary>
    /// The family binding is what makes reuse detection possible: a SUPERSEDED token, whose
    /// hash no longer exists anywhere, must still be able to name the session it came from.
    /// </summary>
    [Fact]
    public void GenerateFor_BindsTheTokenToAFamilyThatSurvivesARoundTrip()
    {
        var family = Guid.NewGuid();

        var token = RefreshTokenProtector.GenerateFor(family);

        Assert.True(RefreshTokenProtector.TryReadFamily(token, out var readBack));
        Assert.Equal(family, readBack);
    }

    /// <summary>
    /// The secret half keeps its full strength — the family id names a row and authenticates
    /// nothing, so it must not be mistaken for entropy. Also pins the wire format, because a
    /// cookie already in a browser has to stay readable by the next deployment.
    /// </summary>
    [Fact]
    public void GenerateFor_KeepsTheFullSecretBehindTheFamilyIdInTheDocumentedFormat()
    {
        var family = Guid.NewGuid();

        var token = RefreshTokenProtector.GenerateFor(family);
        var secret = token[(token.IndexOf('.') + 1)..];

        Assert.StartsWith($"{family:N}.", token, StringComparison.Ordinal);
        Assert.Equal(32, Convert.FromBase64String(secret).Length);
        Assert.NotEqual(token, RefreshTokenProtector.GenerateFor(family));
    }

    /// <summary>
    /// A cookie minted before the family binding existed carries no family and must be reported
    /// as such, so the endpoint falls back to a hash lookup instead of reading 32 characters of
    /// Base64 as a session id. Standard Base64 has no <c>.</c>, which is what makes the
    /// separator unambiguous.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdefX secret")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz.c2VjcmV0")]
    public void TryReadFamily_RejectsAnythingThatIsNotAFamilyBoundToken(string token)
    {
        Assert.False(RefreshTokenProtector.TryReadFamily(token, out var family));
        Assert.Equal(Guid.Empty, family);
    }

    [Fact]
    public void TryReadFamily_RejectsNull()
    {
        Assert.False(RefreshTokenProtector.TryReadFamily(null, out _));
    }

    /// <summary>
    /// Generate() is unchanged on purpose: it is still the unbound secret, and every existing
    /// caller and stored hash keeps working.
    /// </summary>
    [Fact]
    public void Generate_StillProducesAnUnboundSecret()
    {
        Assert.False(RefreshTokenProtector.TryReadFamily(RefreshTokenProtector.Generate(), out _));
    }
}
