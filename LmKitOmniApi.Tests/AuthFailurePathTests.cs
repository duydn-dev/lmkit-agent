using System.Net;
using System.Net.Http.Json;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LmKitOmniApi.Tests;

/// <summary>
/// A real host, a real database, a real <c>AuthController</c>, and a per-test throwaway user,
/// for the authentication FAILURE paths.
///
/// <para><b>Why a fixture at all.</b> Before this file the string <c>api/auth/login</c>
/// appeared in the suite only inside setup helpers: the happy path was exercised a hundred
/// times as a means to an end, and not one test asked what happens when the password is
/// wrong, the account is locked, the refresh token has been rotated, replayed, or revoked.
/// That made authentication the least-covered and most security-relevant surface in the
/// product.</para>
///
/// <para><b>Peer addresses.</b> <c>LoginPolicy</c> is a pure per-IP fixed window (5 permits /
/// 10s) and <c>TestServer</c> leaves <c>RemoteIpAddress</c> null, which collapses every test
/// in the process into ONE bucket — six failed logins in a row would then measure the rate
/// limiter rather than the lockout. So every request carries a distinct impersonated peer via
/// <see cref="TestPeerIpStartupFilter"/>, except where a test is deliberately asserting the
/// limiter. That is not a workaround: an attacker rotating source addresses is exactly the
/// threat account lockout exists to stop, so spreading the failures across addresses also
/// proves the lockout is scoped to the ACCOUNT and not to the caller's IP.</para>
///
/// <para><b>Cookies are handled by hand</b> (<c>HandleCookies = false</c>). These tests need
/// to replay a superseded refresh token and to reuse a JWT after logout, neither of which is
/// expressible through a <c>CookieContainer</c> that helpfully overwrites the old value.</para>
/// </summary>
public sealed class AuthPipelineFixture : IDisposable
{
    private readonly LmKitApiFactory _parent = new();
    private readonly WebApplicationFactory<Program> _host;
    private int _peerCounter;

    public AuthPipelineFixture()
    {
        // Seeds the tenants the throwaway users are attached to, and builds the parent host
        // whose SQLite database the derived host below shares.
        _parent.EnsureSeeded();
        _host = RateLimitTestHost.Create(_parent);
    }

    public HttpClient CreateClient() => _host.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = false
    });

    /// <summary>A peer address no other request in this fixture will ever use.</summary>
    public string NextPeerIp()
    {
        var next = Interlocked.Increment(ref _peerCounter);
        return $"10.{(next >> 16) & 0xFF}.{(next >> 8) & 0xFF}.{next & 0xFF}";
    }

    /// <summary>Inserts a user nothing else in the suite will touch.</summary>
    public async Task<AuthTestUser> CreateUserAsync(string password, bool isActive = true)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = LmKitApiFactory.TenantId,
            Username = $"auth-{Guid.NewGuid():N}",
            Email = $"auth-{Guid.NewGuid():N}@example.test",
            FullName = "Auth Failure Path User",
            Role = "Member",
            IsActive = isActive,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password)
        };

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return new AuthTestUser(user.Id, user.Email, password);
    }

    public async Task<User> ReadUserAsync(Guid userId)
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        return await db.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == userId);
    }

    public async Task<IReadOnlyList<UserSession>> ReadSessionsAsync(Guid userId)
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        return await db.UserSessions.AsNoTracking()
            .Where(session => session.UserId == userId)
            .ToListAsync();
    }

    /// <summary>Edits rows the API has no endpoint for — an expired session, a stale lockout.</summary>
    public async Task MutateAsync(Func<HermesDbContext, Task> mutate)
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        await mutate(db);
        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        _host.Dispose();
        _parent.Dispose();
    }
}

public readonly record struct AuthTestUser(Guid Id, string Email, string Password);

public sealed class AuthFailurePathTests : IClassFixture<AuthPipelineFixture>
{
    private const string CredentialRejection = "Invalid email or password.";
    private const string JwtCookie = "hermes_token";
    private const string RefreshCookie = "hermes_refresh_token";

    /// <summary>
    /// A fragment of the reply that says a token family was revoked for reuse, and the only
    /// thing that distinguishes that 401 from the ordinary "your cookie is stale" one. Matched
    /// as a substring so the surrounding wording can change without breaking these tests.
    /// </summary>
    private const string ReuseRevocationMarker = "Refresh Token được sử dụng lại";

    private readonly AuthPipelineFixture _fixture;

    public AuthFailurePathTests(AuthPipelineFixture fixture) => _fixture = fixture;

    // ---------------------------------------------------------------- login

    [Fact]
    public async Task Login_WithTheWrongPassword_IsRejectedAndCountsAgainstTheAccount()
    {
        var user = await _fixture.CreateUserAsync("Correct-Horse-2026!");
        using var client = _fixture.CreateClient();

        using var response = await LoginAsync(client, user.Email, "definitely-not-the-password");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(ReadCookies(response), cookie => cookie.Key == JwtCookie);
        Assert.Equal(1, (await _fixture.ReadUserAsync(user.Id)).FailedLoginAttempts);
    }

    [Fact]
    public async Task Login_WithAnUnknownEmail_IsIndistinguishableFromAWrongPassword()
    {
        var user = await _fixture.CreateUserAsync("Correct-Horse-2026!");
        using var client = _fixture.CreateClient();

        using var wrongPassword = await LoginAsync(client, user.Email, "wrong");
        using var unknownAccount = await LoginAsync(client, $"nobody-{Guid.NewGuid():N}@example.test", "wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownAccount.StatusCode);
        Assert.Equal(
            await wrongPassword.Content.ReadAsStringAsync(),
            await unknownAccount.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_WithAnOverlongPassword_IsRejectedBeforeTheAccountIsTouched()
    {
        var user = await _fixture.CreateUserAsync("Correct-Horse-2026!");
        using var client = _fixture.CreateClient();

        using var response = await LoginAsync(client, user.Email, new string('x', 129));

        // 400, not 401: the request is malformed, and crucially it must not cost the account
        // one of its five attempts — otherwise a 129-character string is a free lockout.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, (await _fixture.ReadUserAsync(user.Id)).FailedLoginAttempts);
    }

    [Fact]
    public async Task Login_ForADeactivatedAccount_IsRejectedEvenWithTheRightPassword()
    {
        const string password = "Correct-Horse-2026!";
        var user = await _fixture.CreateUserAsync(password, isActive: false);
        using var client = _fixture.CreateClient();

        using var response = await LoginAsync(client, user.Email, password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(ReadCookies(response), cookie => cookie.Key == JwtCookie);
        Assert.Empty(await _fixture.ReadSessionsAsync(user.Id));
    }

    [Fact]
    public async Task Login_LocksTheAccountAfterFiveFailures_AndTheCorrectPasswordThenFails()
    {
        const string password = "Correct-Horse-2026!";
        var user = await _fixture.CreateUserAsync(password);
        using var client = _fixture.CreateClient();

        // Each attempt comes from a different address, so nothing here is the rate limiter:
        // this is the account lockout, and it must not be evadable by rotating source IPs.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var failure = await LoginAsync(client, user.Email, $"wrong-{attempt}");
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }

        var locked = await _fixture.ReadUserAsync(user.Id);
        Assert.Equal(5, locked.FailedLoginAttempts);
        Assert.NotNull(locked.LockoutEnd);
        Assert.True(locked.LockoutEnd > DateTime.UtcNow, "The lockout expired the instant it was set.");

        using var withCorrectPassword = await LoginAsync(client, user.Email, password);
        Assert.Equal(HttpStatusCode.Unauthorized, withCorrectPassword.StatusCode);
        Assert.Empty(await _fixture.ReadSessionsAsync(user.Id));
    }

    [Fact]
    public async Task Login_AfterTheLockoutHasExpired_SucceedsAndClearsTheLockout()
    {
        const string password = "Correct-Horse-2026!";
        var user = await _fixture.CreateUserAsync(password);
        using var client = _fixture.CreateClient();

        await LockOutAsync(user, lockoutEnd: DateTime.UtcNow.AddMinutes(-1));

        using var response = await LoginAsync(client, user.Email, password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var unlocked = await _fixture.ReadUserAsync(user.Id);
        Assert.Equal(0, unlocked.FailedLoginAttempts);
        Assert.Null(unlocked.LockoutEnd);
    }

    [Fact]
    public async Task Login_IsRateLimitedPerAddressIndependentlyOfTheAccount()
    {
        var user = await _fixture.CreateUserAsync("Correct-Horse-2026!");
        using var client = _fixture.CreateClient();
        var attacker = _fixture.NextPeerIp();

        // LoginPolicy permits 5 per 10s per address. Drain it against five DIFFERENT
        // non-existent accounts so the account lockout cannot be what stops the sixth.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var permitted = await LoginAsync(
                client, $"guess-{Guid.NewGuid():N}@example.test", "wrong", peerIp: attacker);
            Assert.Equal(HttpStatusCode.Unauthorized, permitted.StatusCode);
        }

        using var throttled = await LoginAsync(client, user.Email, "wrong", peerIp: attacker);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal(0, (await _fixture.ReadUserAsync(user.Id)).FailedLoginAttempts);
    }

    // ------------------------------------------------------- refresh tokens

    [Fact]
    public async Task Refresh_WithoutACookie_IsRejected()
    {
        using var client = _fixture.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Post, "/api/auth/refresh");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithAFabricatedToken_IsRejected()
    {
        using var client = _fixture.CreateClient();

        using var response = await SendAsync(
            client, HttpMethod.Post, "/api/auth/refresh",
            (RefreshCookie, Convert.ToBase64String(Guid.NewGuid().ToByteArray())));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_RotatesTheRefreshTokenAndKeepsTheSession()
    {
        var session = await SignInAsync();

        using var client = _fixture.CreateClient();
        using var refreshed = await SendAsync(
            client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, session.RefreshToken));

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var rotated = ReadCookies(refreshed);
        Assert.True(rotated.TryGetValue(RefreshCookie, out var newRefreshToken));
        Assert.NotEqual(session.RefreshToken, newRefreshToken);
        Assert.True(rotated.ContainsKey(JwtCookie));

        // The rotated token is the live one.
        using var again = await SendAsync(
            client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, newRefreshToken!));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Refresh_ReplayingASupersededToken_IsRejected()
    {
        var session = await SignInAsync();
        using var client = _fixture.CreateClient();

        using var rotation = await SendAsync(
            client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, session.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, rotation.StatusCode);

        using var replay = await SendAsync(
            client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, session.RefreshToken));

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Refresh_WhenTheSessionHasExpired_IsRejected()
    {
        var session = await SignInAsync();
        await _fixture.MutateAsync(async db =>
        {
            var row = await db.UserSessions.SingleAsync(candidate => candidate.UserId == session.User.Id);
            row.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        });

        using var client = _fixture.CreateClient();
        using var response = await SendAsync(
            client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, session.RefreshToken));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_AfterTheUserIsDeactivated_IsRejected()
    {
        var session = await SignInAsync();
        await _fixture.MutateAsync(async db =>
        {
            var row = await db.Users.SingleAsync(candidate => candidate.Id == session.User.Id);
            row.IsActive = false;
        });

        using var client = _fixture.CreateClient();
        using var response = await SendAsync(
            client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, session.RefreshToken));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------------------ session lifetime

    [Fact]
    public async Task AuthenticatedRequest_AfterLogout_IsRejectedWhileTheJwtIsStillValid()
    {
        var session = await SignInAsync();
        using var client = _fixture.CreateClient();

        using var before = await SendAsync(client, HttpMethod.Get, "/api/auth/me", (JwtCookie, session.JwtToken));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using var logout = await SendAsync(
            client, HttpMethod.Post, "/api/auth/logout",
            (JwtCookie, session.JwtToken), (RefreshCookie, session.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        // The JWT itself has 30 minutes left and is cryptographically intact. Only the
        // revocation checks in OnTokenValidated stand between it and the API.
        using var after = await SendAsync(client, HttpMethod.Get, "/api/auth/me", (JwtCookie, session.JwtToken));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Refresh_AfterLogout_IsRejected()
    {
        var session = await SignInAsync();
        using var client = _fixture.CreateClient();

        using var logout = await SendAsync(
            client, HttpMethod.Post, "/api/auth/logout",
            (JwtCookie, session.JwtToken), (RefreshCookie, session.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        using var response = await SendAsync(
            client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, session.RefreshToken));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedRequest_AfterTheSessionIsRevokedOutOfBand_IsRejected()
    {
        var session = await SignInAsync();
        await _fixture.MutateAsync(async db =>
        {
            var row = await db.UserSessions.SingleAsync(candidate => candidate.UserId == session.User.Id);
            row.Status = "revoked";
            row.RevokedAtUtc = DateTime.UtcNow;
        });

        using var client = _fixture.CreateClient();
        using var response = await SendAsync(client, HttpMethod.Get, "/api/auth/me", (JwtCookie, session.JwtToken));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedRequest_AfterTheUserIsDeactivated_IsRejected()
    {
        var session = await SignInAsync();
        await _fixture.MutateAsync(async db =>
        {
            var row = await db.Users.SingleAsync(candidate => candidate.Id == session.User.Id);
            row.IsActive = false;
        });

        using var client = _fixture.CreateClient();
        using var response = await SendAsync(client, HttpMethod.Get, "/api/auth/me", (JwtCookie, session.JwtToken));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------- security-expectation probes
    //
    // The three tests below were written by the change that added this file as REPRODUCERS for
    // defects that existed in AuthController. Each was written to the security bar the endpoint
    // should meet, run against the real pipeline, and observed to fail; each was then Skipped
    // rather than left red, because this suite is the regression signal for four other work
    // streams and a permanently failing test trains people to ignore the colour.
    //
    // All three now pass against a fixed AuthController. None of their assertions was relaxed
    // to get there. What each one produced on the unfixed code, for the record:
    //
    //   Login_AgainstALockedAccount_DoesNotRevealThatTheAccountExists
    //     Assert.Equal() Failure: Strings differ, ↓ (pos 12)
    //     Expected: "{"message":"Invalid email or password."}"
    //     Actual:   "{"message":"Tài khoản đã bị khóa tạm thời"···
    //
    //   Login_OneWrongPasswordAfterALockoutExpires_DoesNotImmediatelyRelock
    //     A single failed attempt after the lockout window had already expired re-locked the
    //     account for another 15 minutes, because FailedLoginAttempts is never reset when a
    //     lockout expires — only a SUCCESSFUL login clears it.
    //
    //   Refresh_ReplayingASupersededToken_RevokesTheSession
    //     Assert.Equal() Failure: Values differ / Expected: Unauthorized / Actual: OK
    //
    // The tests AFTER them cover what the fixes introduced rather than what they removed.

    [Fact]
    public async Task Login_AgainstALockedAccount_DoesNotRevealThatTheAccountExists()
    {
        const string password = "Correct-Horse-2026!";
        var user = await _fixture.CreateUserAsync(password);
        using var client = _fixture.CreateClient();
        await LockOutAsync(user, lockoutEnd: DateTime.UtcNow.AddMinutes(15));

        using var locked = await LoginAsync(client, user.Email, "wrong");
        using var unknown = await LoginAsync(client, $"nobody-{Guid.NewGuid():N}@example.test", "wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(
            await unknown.Content.ReadAsStringAsync(),
            await locked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_OneWrongPasswordAfterALockoutExpires_DoesNotImmediatelyRelock()
    {
        const string password = "Correct-Horse-2026!";
        var user = await _fixture.CreateUserAsync(password);
        using var client = _fixture.CreateClient();
        await LockOutAsync(user, lockoutEnd: DateTime.UtcNow.AddMinutes(-1));

        using var single = await LoginAsync(client, user.Email, "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, single.StatusCode);

        var after = await _fixture.ReadUserAsync(user.Id);
        Assert.True(
            after.LockoutEnd is null || after.LockoutEnd <= DateTime.UtcNow,
            "A single failed attempt after the lockout window had already expired re-locked the "
            + "account for another 15 minutes, because FailedLoginAttempts is never reset when a "
            + "lockout expires — only a SUCCESSFUL login clears it.");
    }

    [Fact]
    public async Task Refresh_ReplayingASupersededToken_RevokesTheSession()
    {
        var session = await SignInAsync();
        using var client = _fixture.CreateClient();

        using var rotation = await SendAsync(
            client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, session.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, rotation.StatusCode);
        var rotated = ReadCookies(rotation)[RefreshCookie];

        using var replay = await SendAsync(
            client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, session.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // OAuth 2.0 Security BCP §4.14.2: a replayed refresh token is evidence that the token
        // leaked, and the only party who can tell which copy is the thief's is nobody — so the
        // whole grant must be revoked. Rejecting the replay alone leaves the thief holding the
        // ROTATED token, which is the one that still works.
        using var stillLive = await SendAsync(
            client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, rotated));
        Assert.Equal(HttpStatusCode.Unauthorized, stillLive.StatusCode);
    }

    // ------------------------------------- what the fixes introduced (D1/D2)

    /// <summary>
    /// The body is the obvious half of an enumeration fix and the easy half to get right. This
    /// pins the rest of the observable response: the status line and every header the
    /// application controls. A <c>Content-Length</c> or a <c>Content-Type</c> that differed
    /// would enumerate accounts just as well as a different message.
    /// </summary>
    [Fact]
    public async Task Login_TheTwo401s_AreIndistinguishableInStatusHeadersAndBody()
    {
        const string password = "Correct-Horse-2026!";
        var user = await _fixture.CreateUserAsync(password);
        using var client = _fixture.CreateClient();
        await LockOutAsync(user, lockoutEnd: DateTime.UtcNow.AddMinutes(15));

        using var locked = await LoginAsync(client, user.Email, "wrong");
        using var unknown = await LoginAsync(client, $"nobody-{Guid.NewGuid():N}@example.test", "wrong");

        Assert.Equal(unknown.StatusCode, locked.StatusCode);
        Assert.Equal(unknown.ReasonPhrase, locked.ReasonPhrase);
        Assert.Equal(
            await unknown.Content.ReadAsStringAsync(),
            await locked.Content.ReadAsStringAsync());
        Assert.Equal(ComparableHeaders(unknown), ComparableHeaders(locked));
    }

    /// <summary>
    /// The other half of the enumeration fix: the account owner must still be told. They learn
    /// it by presenting the CORRECT password, which a stranger probing for valid addresses
    /// cannot do — so this reply can be specific without being an oracle.
    /// </summary>
    [Fact]
    public async Task Login_WithTheCorrectPasswordOnALockedAccount_TellsTheOwnerTheyAreLockedOut()
    {
        const string password = "Correct-Horse-2026!";
        var user = await _fixture.CreateUserAsync(password);
        using var client = _fixture.CreateClient();
        await LockOutAsync(user, lockoutEnd: DateTime.UtcNow.AddMinutes(15));

        using var response = await LoginAsync(client, user.Email, password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(CredentialRejection, body, StringComparison.Ordinal);
        Assert.Contains("khóa", body, StringComparison.Ordinal);
        // Still no session, and the lockout is not extended by the correct password either.
        Assert.Empty(await _fixture.ReadSessionsAsync(user.Id));
    }

    /// <summary>
    /// Failing WHILE locked must cost nothing. If it bumped the counter or pushed
    /// <c>LockoutEnd</c> forward, the denial of service the counter fix removed would simply
    /// come back through the other door — a stranger keeps the account shut by failing once
    /// inside every window instead of once after every window.
    /// </summary>
    [Fact]
    public async Task Login_FailingWhileAlreadyLockedOut_DoesNotExtendTheLockout()
    {
        const string password = "Correct-Horse-2026!";
        var user = await _fixture.CreateUserAsync(password);
        using var client = _fixture.CreateClient();
        var lockoutEnd = DateTime.UtcNow.AddMinutes(15);
        await LockOutAsync(user, lockoutEnd);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var failure = await LoginAsync(client, user.Email, $"wrong-{attempt}");
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }

        var after = await _fixture.ReadUserAsync(user.Id);
        Assert.Equal(5, after.FailedLoginAttempts);
        Assert.NotNull(after.LockoutEnd);
        // Same instant it was set to, to the second — nothing moved it.
        Assert.True(
            Math.Abs((after.LockoutEnd!.Value - lockoutEnd).TotalSeconds) < 1,
            $"Three failures during the lockout moved LockoutEnd from {lockoutEnd:O} to {after.LockoutEnd:O}.");
    }

    /// <summary>
    /// The counter fix must not weaken the lockout it is fixing. After a window closes the
    /// account gets its FULL budget back — not an unlimited one: five more failures lock it
    /// again. That is the property that stops credential stuffing, and it survives.
    /// </summary>
    [Fact]
    public async Task Login_AfterALockoutExpires_RestoresTheFullBudgetAndStillRelocksOnTheFifthFailure()
    {
        const string password = "Correct-Horse-2026!";
        var user = await _fixture.CreateUserAsync(password);
        using var client = _fixture.CreateClient();
        await LockOutAsync(user, lockoutEnd: DateTime.UtcNow.AddMinutes(-1));

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var failure = await LoginAsync(client, user.Email, $"wrong-{attempt}");
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);

            var during = await _fixture.ReadUserAsync(user.Id);
            Assert.Equal(attempt, during.FailedLoginAttempts);
            Assert.Null(during.LockoutEnd);
        }

        using var fifth = await LoginAsync(client, user.Email, "wrong-5");
        Assert.Equal(HttpStatusCode.Unauthorized, fifth.StatusCode);

        var relocked = await _fixture.ReadUserAsync(user.Id);
        Assert.Equal(5, relocked.FailedLoginAttempts);
        Assert.NotNull(relocked.LockoutEnd);
        Assert.True(relocked.LockoutEnd > DateTime.UtcNow, "The fifth failure did not re-lock the account.");
    }

    // ------------------------------------------ what the fix introduced (D3)

    /// <summary>
    /// Revoking the family has to kill the ACCESS token too, or the thief keeps calling the API
    /// for the remainder of the JWT's thirty minutes and only loses the ability to renew. It
    /// does, because <c>OnTokenValidated</c> re-reads the session on every request — this test
    /// is what proves the two halves are actually wired together.
    /// </summary>
    [Fact]
    public async Task Refresh_ReuseRevocation_AlsoKillsTheAccessTokenMintedFromThatSession()
    {
        var session = await SignInAsync();
        using var client = _fixture.CreateClient();

        using var beforeAnything = await SendAsync(client, HttpMethod.Get, "/api/auth/me", (JwtCookie, session.JwtToken));
        Assert.Equal(HttpStatusCode.OK, beforeAnything.StatusCode);

        using var rotation = await SendAsync(
            client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, session.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, rotation.StatusCode);

        using var replay = await SendAsync(
            client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, session.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        var stored = Assert.Single(await _fixture.ReadSessionsAsync(session.User.Id));
        Assert.Equal("revoked", stored.Status);
        Assert.NotNull(stored.RevokedAtUtc);
        Assert.Null(stored.RefreshTokenHash);

        using var afterRevocation = await SendAsync(client, HttpMethod.Get, "/api/auth/me", (JwtCookie, session.JwtToken));
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevocation.StatusCode);
    }

    /// <summary>
    /// A logout is not a breach. The refresh a browser had already queued when the user pressed
    /// "sign out" must be answered with the ordinary rejection, not with the alarming one — and
    /// it must not be attributed to token theft in the log either.
    /// </summary>
    [Fact]
    public async Task Refresh_AfterLogout_IsRejectedWithoutBeingCalledReuse()
    {
        var session = await SignInAsync();
        using var client = _fixture.CreateClient();

        using var logout = await SendAsync(
            client, HttpMethod.Post, "/api/auth/logout",
            (JwtCookie, session.JwtToken), (RefreshCookie, session.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        using var response = await SendAsync(
            client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, session.RefreshToken));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(ReuseRevocationMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Revocation is scoped to the family that was replayed. One stolen token must not sign
    /// every other user out — including the same user's other devices, which are separate
    /// sessions and therefore separate families.
    /// </summary>
    [Fact]
    public async Task Refresh_ReuseRevocation_TouchesOnlyTheFamilyThatWasReplayed()
    {
        var breached = await SignInAsync();
        var bystander = await SignInAsync();
        using var client = _fixture.CreateClient();

        using var rotation = await SendAsync(
            client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, breached.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, rotation.StatusCode);

        using var replay = await SendAsync(
            client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, breached.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Contains(ReuseRevocationMarker, await replay.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal("revoked", Assert.Single(await _fixture.ReadSessionsAsync(breached.User.Id)).Status);
        Assert.Equal("active", Assert.Single(await _fixture.ReadSessionsAsync(bystander.User.Id)).Status);

        using var unaffected = await SendAsync(
            client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, bystander.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, unaffected.StatusCode);
    }

    /// <summary>
    /// The family is the session row, and it is what stays put while the token underneath it
    /// changes. Rotating repeatedly must never look like reuse to the endpoint: this walks a
    /// chain of five rotations and demands the same session survives all of them, still active.
    /// </summary>
    [Fact]
    public async Task Refresh_RotatingRepeatedly_KeepsOneFamilyAliveAcrossEveryRotation()
    {
        var session = await SignInAsync();
        using var client = _fixture.CreateClient();

        var seen = new HashSet<string>(StringComparer.Ordinal) { session.RefreshToken };
        var current = session.RefreshToken;
        var familyId = Assert.Single(await _fixture.ReadSessionsAsync(session.User.Id)).Id;

        for (var rotation = 0; rotation < 5; rotation++)
        {
            using var refreshed = await SendAsync(
                client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, current));
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

            current = ReadCookies(refreshed)[RefreshCookie];
            Assert.True(seen.Add(current), "A rotation re-issued a refresh token that had already been used.");

            var row = Assert.Single(await _fixture.ReadSessionsAsync(session.User.Id));
            Assert.Equal(familyId, row.Id);
            Assert.Equal("active", row.Status);
        }
    }

    /// <summary>
    /// The race the reuse rule must not lose to.
    ///
    /// <para>Several requests arrive holding the SAME, current refresh token — two browser tabs
    /// whose 401s coincided, a retried request. Exactly one may rotate; the others must fail
    /// WITHOUT being read as theft, because losing a rotation race is the one thing a
    /// well-behaved client can do. That is why <c>rotated != 1</c> is answered with the plain
    /// rejection and never with a revocation.</para>
    ///
    /// <para>The assertions are the invariants that hold under EVERY interleaving, which is
    /// what keeps this test honest rather than lucky: exactly one caller may win; a family may
    /// be revoked at most once, never in a cascade; and the row's final state must agree with
    /// what the callers were told. In practice the observed run reports zero revocations — the
    /// requests all read the row before the winner committed — and a revocation here would mean
    /// one request was late enough to see a genuinely superseded token, which is by design the
    /// same thing a replay looks like.</para>
    /// </summary>
    [Fact]
    public async Task Refresh_ConcurrentRefreshesOfTheCurrentToken_ProduceOneWinnerAndNoCascade()
    {
        const int callers = 6;
        var session = await SignInAsync();
        using var client = _fixture.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, callers).Select(_ =>
            SendAsync(client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, session.RefreshToken))));

        try
        {
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.All(
                responses.Where(response => response.StatusCode != HttpStatusCode.OK),
                response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));

            var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));
            var revocations = bodies.Count(body => body.Contains(ReuseRevocationMarker, StringComparison.Ordinal));
            Assert.InRange(revocations, 0, 1);

            var row = Assert.Single(await _fixture.ReadSessionsAsync(session.User.Id));
            Assert.Equal(revocations == 0 ? "active" : "revoked", row.Status);

            if (revocations == 0)
            {
                // The family survived, so the winner's token — and only the winner's — is live.
                var winner = responses.Single(response => response.StatusCode == HttpStatusCode.OK);
                using var stillWorks = await SendAsync(
                    client, HttpMethod.Post, "/api/auth/refresh", (RefreshCookie, ReadCookies(winner)[RefreshCookie]));
                Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
            }
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    // ------------------------------------------------------------- helpers

    private sealed record SignedInSession(AuthTestUser User, string JwtToken, string RefreshToken);

    private async Task<SignedInSession> SignInAsync()
    {
        const string password = "Correct-Horse-2026!";
        var user = await _fixture.CreateUserAsync(password);
        using var client = _fixture.CreateClient();

        using var login = await LoginAsync(client, user.Email, password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var cookies = ReadCookies(login);
        return new SignedInSession(user, cookies[JwtCookie], cookies[RefreshCookie]);
    }

    private async Task LockOutAsync(AuthTestUser user, DateTime lockoutEnd) =>
        await _fixture.MutateAsync(async db =>
        {
            var row = await db.Users.SingleAsync(candidate => candidate.Id == user.Id);
            row.FailedLoginAttempts = 5;
            row.LockoutEnd = lockoutEnd;
        });

    private Task<HttpResponseMessage> LoginAsync(
        HttpClient client, string email, string password, string? peerIp = null)
    {
        var request = BuildRequest(HttpMethod.Post, "/api/auth/login", peerIp);
        request.Content = JsonContent.Create(new { email, password });
        return client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, params (string Name, string Value)[] cookies)
    {
        var request = BuildRequest(method, url, peerIp: null);
        if (cookies.Length > 0)
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}")));
        return client.SendAsync(request);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string url, string? peerIp)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestPeerIpStartupFilter.HeaderName, peerIp ?? _fixture.NextPeerIp());
        return request;
    }

    /// <summary>
    /// Every response header the application decides, flattened and ordered so two responses
    /// can be compared for equality. <c>Date</c> is dropped because it is the clock, not a
    /// decision; everything else — including <c>Content-Type</c> and <c>Content-Length</c>,
    /// which a differing message body would give away — is compared.
    /// </summary>
    private static string ComparableHeaders(HttpResponseMessage response) =>
        string.Join(
            "\n",
            response.Headers.Concat(response.Content.Headers)
                .Where(header => !header.Key.Equals("Date", StringComparison.OrdinalIgnoreCase))
                .Select(header => $"{header.Key}: {string.Join(",", header.Value)}")
                .OrderBy(line => line, StringComparer.Ordinal));

    /// <summary>Name → value of every <c>Set-Cookie</c> on the response, attributes stripped.</summary>
    private static Dictionary<string, string> ReadCookies(HttpResponseMessage response)
    {
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return cookies;

        foreach (var header in values)
        {
            var pair = header.Split(';', 2)[0];
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;

            var name = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();
            // A deletion is emitted as `name=; expires=<past>`; it is not a credential.
            if (value.Length == 0) continue;
            cookies[name] = value;
        }

        return cookies;
    }
}
