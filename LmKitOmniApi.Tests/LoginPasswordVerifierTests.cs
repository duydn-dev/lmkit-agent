using System.Diagnostics;
using LmKitOmniApi.Infrastructure.Security;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Making two 401s byte-identical is only half of an account-enumeration fix. The other half is
/// that they must also take the same amount of time, and the only expensive thing a login does
/// is hash the password — so an endpoint that skips the hash when the account is missing leaks
/// through a stopwatch exactly what it stopped leaking through the body.
/// </summary>
public class LoginPasswordVerifierTests
{
    private const string Password = "Correct-Horse-2026!";

    /// <summary>
    /// A BCrypt verify at this codebase's work factor costs tens of milliseconds; a short
    /// circuit costs microseconds. The floor sits an order of magnitude below the real cost and
    /// three orders above a skip, so it separates the two without depending on how fast the
    /// machine is. The MINIMUM of several runs is used because scheduling noise can only ADD
    /// time — a floor is the one statistic noise cannot fake.
    /// </summary>
    private static readonly TimeSpan HashingFloor = TimeSpan.FromMilliseconds(5);

    [Fact]
    public void Verify_AcceptsTheCorrectPassword()
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(Password);

        Assert.True(LoginPasswordVerifier.Verify(Password, hash));
    }

    [Fact]
    public void Verify_RejectsTheWrongPassword()
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(Password);

        Assert.False(LoginPasswordVerifier.Verify("something-else", hash));
    }

    /// <summary>A hash this build cannot verify is a rejection, never an accidental accept.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plaintext-password")]
    [InlineData("$1$legacy$md5crypt")]
    [InlineData("$2a$11$this-is-not-a-real-bcrypt-hash")]
    public void Verify_RejectsEveryHashItCannotUnderstand(string? storedHash)
    {
        Assert.False(LoginPasswordVerifier.Verify(Password, storedHash));
    }

    /// <summary>
    /// The missing-account path must do the SAME work as a real one. This is the assertion the
    /// timing side of the enumeration fix rests on: if someone reintroduces an early return for
    /// a null hash, this goes red rather than the defect going quiet.
    /// </summary>
    [Fact]
    public void Verify_WithNoStoredHash_StillPaysTheFullHashingCost()
    {
        // Warm the decoy and the BCrypt code paths so the measurement is of the work, not of
        // first-call initialisation.
        LoginPasswordVerifier.Verify(Password, null);

        Assert.True(
            MinimumDurationOf(() => LoginPasswordVerifier.Verify(Password, null)) >= HashingFloor,
            "Verifying against a missing account returned faster than a password hash can be "
            + "computed, so the login endpoint's two 401 replies are separable by response time "
            + "even though their bodies are identical.");
    }

    /// <summary>
    /// And the same for a stored hash the build cannot parse — a legacy row must not become a
    /// second, quieter oracle.
    /// </summary>
    [Fact]
    public void Verify_WithAnUnsupportedHash_StillPaysTheFullHashingCost()
    {
        LoginPasswordVerifier.Verify(Password, "plaintext-password");

        Assert.True(
            MinimumDurationOf(() => LoginPasswordVerifier.Verify(Password, "plaintext-password")) >= HashingFloor,
            "An account whose stored hash is not BCrypt is answered faster than one whose hash "
            + "is, which distinguishes the two from outside.");
    }

    private static TimeSpan MinimumDurationOf(Action work)
    {
        var shortest = TimeSpan.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            var stopwatch = Stopwatch.StartNew();
            work();
            stopwatch.Stop();
            if (stopwatch.Elapsed < shortest) shortest = stopwatch.Elapsed;
        }

        return shortest;
    }
}
