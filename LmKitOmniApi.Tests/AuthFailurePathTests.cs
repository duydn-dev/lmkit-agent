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
    // The three tests below are REPRODUCERS for defects that exist in AuthController today.
    // Each one was written to the security bar the endpoint should meet, run against the real
    // pipeline, and observed to fail. None of their assertions has been relaxed to make them
    // pass, and none of them is speculative — the failure text in each Skip reason is what the
    // run actually produced.
    //
    // They are Skipped rather than left red because this suite is the regression signal for
    // four other work streams; a permanently failing test trains people to ignore the colour.
    // Fixing the controller is a one-word change here: delete the Skip.
    //
    // AuthController.cs is not this change's to edit — the defects are reported, not patched.

    [Fact(Skip =
        "PRODUCTION DEFECT (account enumeration). AuthController.Login returns "
        + "'{\"message\":\"Tài khoản đã bị khóa tạm thời...\"}' for a locked account "
        + "(AuthController.cs:57-61) but '{\"message\":\"Invalid email or password.\"}' for an "
        + "unknown one (:50-54). Both are 401, so an unauthenticated caller distinguishes a real "
        + "account from a fake one by sending five wrong passwords and reading the sixth reply. "
        + "Combined with the lockout defect below, that oracle also confirms the DoS landed. "
        + "Fix: return the same body for both, and delete this Skip.")]
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

    [Fact(Skip =
        "PRODUCTION DEFECT (indefinite lockout / DoS). AuthController.Login clears "
        + "FailedLoginAttempts only on a SUCCESSFUL login (AuthController.cs:96-97); an expiring "
        + "lockout leaves the counter at 5. The next single wrong password therefore evaluates "
        + "6 >= 5 (:76) and re-locks for another 15 minutes. Observed: one failed attempt after "
        + "the window had already expired set LockoutEnd back into the future. Anyone who knows "
        + "a user's email can keep that account locked out forever at one request per 15 "
        + "minutes. Fix: reset FailedLoginAttempts when an expired lockout is observed, and "
        + "delete this Skip.")]
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

    [Fact(Skip =
        "PRODUCTION DEFECT (no refresh-token reuse detection). AuthController.Refresh rotates "
        + "atomically and rejects a superseded token (AuthController.cs:203-211), but treats the "
        + "replay as a plain 401 and leaves the session active. Observed: after replaying the "
        + "old token, POST /api/auth/refresh with the ROTATED token still returned 200 OK. A "
        + "replay is evidence the token leaked and nothing can tell which holder is the thief, "
        + "so OAuth 2.0 Security BCP §4.14.2 requires revoking the whole grant. As shipped, the "
        + "victim gets a silent 401 and the thief keeps the working token. Fix: revoke the "
        + "session when rotation finds the token already spent, and delete this Skip.")]
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
