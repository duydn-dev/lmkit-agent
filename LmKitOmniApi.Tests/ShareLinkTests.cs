using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LmKitOmniApi.Application.Share;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LmKitOmniApi.Tests;

public sealed class ShareLinkTests : IClassFixture<LmKitApiFactory>
{
    // Sessions seeded by LmKitApiFactory.EnsureSeeded: 5555… belongs to the
    // integration user, 6666… to a different user in a different tenant.
    private static readonly Guid OwnSessionId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid OtherSessionId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    /// <summary>Created on demand by <see cref="EnsureSpareSessionAsync"/>, not by the factory.</summary>
    private static readonly Guid SpareSessionId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private readonly LmKitApiFactory _factory;

    public ShareLinkTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
        EnsureConversationSeeded();
    }

    [Fact]
    public async Task CreateShareLink_ThenAnonymousGet_ReturnsTranscriptAndStampsBoundedDeadline()
    {
        using var owner = await CreateAuthenticatedClientAsync();
        using var anonymous = _factory.CreateClient();

        var before = DateTime.UtcNow;
        var created = await owner.PostAsync($"/api/share/chat-sessions/{OwnSessionId}", null);
        var after = DateTime.UtcNow;
        // The response body is a one-shot stream: read it once and use it for both the
        // token and the deadline.
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var token = createdBody.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        var shared = await anonymous.GetAsync($"/api/share/chat/{token}");
        var payload = await shared.Content.ReadFromJsonAsync<JsonElement>();

        // Raw token: 32 random bytes, base64url without padding → exactly 43 URL-safe chars.
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);

        // Minting bounds the link and says so. The owner is the only person who can act
        // on the deadline — they are the one who must re-share before it lapses — so it
        // has to be in the response, not merely in the row.
        var reportedDeadline = AsUtc(createdBody.GetProperty("expiresAtUtc"));
        var ttl = TimeSpan.FromDays(ChatShareLink.DefaultTimeToLiveDays);
        Assert.InRange(reportedDeadline, before + ttl, after + ttl);

        Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
        Assert.Equal("Own session", payload.GetProperty("title").GetString());
        Assert.True(payload.TryGetProperty("createdAt", out _));
        var messages = payload.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(2, messages.Length); // the seeded "system" message is filtered out
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("Hello there", messages[0].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("Hi! How can I help?", messages[1].GetProperty("content").GetString());
        Assert.All(messages, message => Assert.True(message.TryGetProperty("createdAt", out _)));

        // Persistence stores only the SHA-256 hex digest — never the raw token — and the
        // deadline in the response is the one that was actually written.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        var stored = await db.ChatShareLinks.AsNoTracking()
            .Where(link => link.ChatSessionId == OwnSessionId && link.RevokedAtUtc == null)
            .Select(link => new { link.TokenHash, link.ExpiresAtUtc })
            .SingleAsync();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), stored.TokenHash);
        Assert.NotEqual(token, stored.TokenHash);
        Assert.Equal(reportedDeadline, DateTime.SpecifyKind(stored.ExpiresAtUtc, DateTimeKind.Utc));
    }

    [Fact]
    public async Task RevokeShareLinks_MakesExistingTokenReturnGoneWithRevokedReason()
    {
        using var owner = await CreateAuthenticatedClientAsync();
        using var anonymous = _factory.CreateClient();

        var created = await owner.PostAsync($"/api/share/chat-sessions/{OwnSessionId}", null);
        var token = await ReadTokenAsync(created);
        var beforeRevoke = await anonymous.GetAsync($"/api/share/chat/{token}");
        var revoke = await owner.DeleteAsync($"/api/share/chat-sessions/{OwnSessionId}");
        var afterRevoke = await anonymous.GetAsync($"/api/share/chat/{token}");
        var body = await afterRevoke.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, beforeRevoke.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        // CONTRACT CHANGE. A revoked token used to be a bare 404, deliberately
        // indistinguishable from a token that never existed. Adding a deadline made that
        // untenable: "đã thu hồi" and "hết hạn" are different events with different
        // remedies, and a recipient who cannot tell either from a mistyped URL is being
        // told nothing useful. A revoked link is now 410 Gone naming the reason.
        //
        // What the old invariant actually protected — enumeration — is unaffected: see
        // UnknownToken_StaysOpaque below. Reaching this 410 requires already holding a
        // genuine 256-bit token, i.e. already knowing the link existed.
        Assert.Equal(HttpStatusCode.Gone, afterRevoke.StatusCode);
        Assert.Equal("revoked", body.GetProperty("reason").GetString());
        Assert.True(body.TryGetProperty("revokedAtUtc", out _));
        // A refusal hands over nothing, whatever its reason.
        Assert.False(body.TryGetProperty("messages", out _));
        Assert.False(body.TryGetProperty("title", out _));
    }

    /// <summary>
    /// The core of this change: a link nobody revoked stops working on its own. Before
    /// the deadline existed this token would still have served the whole transcript.
    /// </summary>
    [Fact]
    public async Task ExpiredLink_IsRefusedAsGoneWithExpiredReason()
    {
        using var anonymous = _factory.CreateClient();

        // Deadline in the past, RevokedAtUtc left NULL: the ONLY thing refusing this
        // link is the clock. On the pre-change code this token served the transcript.
        var deadline = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var token = await SeedLinkAsync(expiresAtUtc: deadline);

        var response = await anonymous.GetAsync($"/api/share/chat/{token}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("expired", body.GetProperty("reason").GetString());
        Assert.Equal(deadline, AsUtc(body.GetProperty("expiredAtUtc")));
        // Refused means refused: no transcript leaks through the expired branch either.
        Assert.False(body.TryGetProperty("messages", out _));
        Assert.False(body.TryGetProperty("title", out _));
    }

    /// <summary>
    /// The two refusals must not be interchangeable — that is the whole point of
    /// splitting them. Same status, same emptiness, different reason.
    /// </summary>
    [Fact]
    public async Task ExpiredAndRevoked_AreRefusedAlikeButNamedDifferently()
    {
        using var anonymous = _factory.CreateClient();

        // Two links differing in exactly one field, so the reason the API gives back can
        // only have come from that field.
        var expiredToken = await SeedLinkAsync(expiresAtUtc: DateTime.UtcNow.AddDays(-1));
        var revokedToken = await SeedLinkAsync(
            expiresAtUtc: DateTime.UtcNow.AddDays(30),
            revokedAtUtc: DateTime.UtcNow.AddMinutes(-5));

        var expired = await anonymous.GetAsync($"/api/share/chat/{expiredToken}");
        var revoked = await anonymous.GetAsync($"/api/share/chat/{revokedToken}");
        var expiredBody = await expired.Content.ReadFromJsonAsync<JsonElement>();
        var revokedBody = await revoked.Content.ReadFromJsonAsync<JsonElement>();

        // Refused the same way...
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, revoked.StatusCode);
        // ...but the caller is told which.
        Assert.Equal("expired", expiredBody.GetProperty("reason").GetString());
        Assert.Equal("revoked", revokedBody.GetProperty("reason").GetString());
        Assert.NotEqual(
            expiredBody.GetProperty("reason").GetString(),
            revokedBody.GetProperty("reason").GetString());
    }

    /// <summary>
    /// An unknown token stays as opaque as it ever was — no status change, no reason, no
    /// timestamp. The enumeration property the old blanket 404 was defending still holds.
    /// </summary>
    [Fact]
    public async Task UnknownToken_StaysOpaqueNotFound()
    {
        using var anonymous = _factory.CreateClient();

        var neverIssued = await anonymous.GetAsync($"/api/share/chat/{new string('z', 43)}");
        var absurd = await anonymous.GetAsync($"/api/share/chat/{new string('z', 400)}");

        Assert.Equal(HttpStatusCode.NotFound, neverIssued.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, absurd.StatusCode);
        Assert.DoesNotContain("reason", await neverIssued.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A link still inside its window resolves normally, right up to the deadline — the
    /// fix must bound the window, not shrink it to nothing.
    /// </summary>
    [Fact]
    public async Task LinkInsideItsWindow_StillResolves()
    {
        using var anonymous = _factory.CreateClient();

        var token = await SeedLinkAsync(expiresAtUtc: DateTime.UtcNow.AddMinutes(5));

        var response = await anonymous.GetAsync($"/api/share/chat/{token}");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Spare session", payload.GetProperty("title").GetString());
        Assert.NotEmpty(payload.GetProperty("messages").EnumerateArray());
    }

    /// <summary>
    /// The backfill's two outcomes, as the migration leaves them. The UPDATE itself runs
    /// only on PostgreSQL (these tests build their schema with EnsureCreated and never
    /// run migrations), so what is asserted here is its EFFECT: a legacy row given a
    /// grace-window deadline keeps working, and one whose natural window had already
    /// closed does not — and is refused as expired, never as revoked, because the
    /// backfill never touches RevokedAtUtc.
    /// </summary>
    [Theory]
    [InlineData(14, true)]   // inside the 14-day floor the migration grants stale rows
    [InlineData(-1, false)]  // past its window: bounded, and therefore now refused
    public async Task BackfilledLegacyLink_ResolvesOnlyInsideItsGrantedWindow(int daysFromNow, bool expectResolvable)
    {
        using var anonymous = _factory.CreateClient();

        // A row exactly as the migration leaves it: minted long before the deadline
        // existed (CreatedAtUtc is 400 days back inside the helper), never revoked, and
        // now carrying a backfilled ExpiresAtUtc.
        var rawToken = await SeedLinkAsync(expiresAtUtc: DateTime.UtcNow.AddDays(daysFromNow));

        var response = await anonymous.GetAsync($"/api/share/chat/{rawToken}");

        if (expectResolvable)
        {
            // Nothing anyone was using dies on deploy day — the reason the floor exists.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        else
        {
            // And nothing is grandfathered as permanent either.
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
            Assert.Equal("expired", body.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task ShareManagement_ForForeignOrMissingSession_ReturnsNotFound()
    {
        using var owner = await CreateAuthenticatedClientAsync();

        var foreignCreate = await owner.PostAsync($"/api/share/chat-sessions/{OtherSessionId}", null);
        var missingCreate = await owner.PostAsync($"/api/share/chat-sessions/{Guid.NewGuid()}", null);
        var foreignRevoke = await owner.DeleteAsync($"/api/share/chat-sessions/{OtherSessionId}");

        // Never 403: a foreign session must look exactly like one that does not exist.
        Assert.Equal(HttpStatusCode.NotFound, foreignCreate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingCreate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignRevoke.StatusCode);
    }

    [Fact]
    public async Task CreateShareLink_Twice_RotatesTokenAndInvalidatesTheOldOne()
    {
        using var owner = await CreateAuthenticatedClientAsync();
        using var anonymous = _factory.CreateClient();

        var first = await owner.PostAsync($"/api/share/chat-sessions/{OwnSessionId}", null);
        var firstToken = await ReadTokenAsync(first);
        var second = await owner.PostAsync($"/api/share/chat-sessions/{OwnSessionId}", null);
        var secondToken = await ReadTokenAsync(second);
        var oldTokenResponse = await anonymous.GetAsync($"/api/share/chat/{firstToken}");
        var newTokenResponse = await anonymous.GetAsync($"/api/share/chat/{secondToken}");

        Assert.NotEqual(firstToken, secondToken);
        // Rotation revokes, so the superseded URL now says so rather than pretending it
        // never existed — "the owner replaced this link" is the honest answer to someone
        // holding it.
        Assert.Equal(HttpStatusCode.Gone, oldTokenResponse.StatusCode);
        var oldBody = await oldTokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("revoked", oldBody.GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.OK, newTokenResponse.StatusCode);
    }

    [Fact]
    public async Task ShareManagementEndpoints_RejectAnonymousCallers()
    {
        using var anonymous = _factory.CreateClient();

        var create = await anonymous.PostAsync($"/api/share/chat-sessions/{OwnSessionId}", null);
        var revoke = await anonymous.DeleteAsync($"/api/share/chat-sessions/{OwnSessionId}");

        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, revoke.StatusCode);
    }

    /// <summary>
    /// Idempotently gives the owned session a deterministic transcript (system + user +
    /// assistant with fixed timestamps) so the public payload has content to assert on.
    /// </summary>
    private void EnsureConversationSeeded()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        if (db.ChatMessages.Any(message => message.ChatSessionId == OwnSessionId)) return;

        var baseline = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.ChatMessages.AddRange(
            new ChatMessage
            {
                ChatSessionId = OwnSessionId,
                Role = "system",
                Content = "Internal system prompt",
                CreatedAt = baseline
            },
            new ChatMessage
            {
                ChatSessionId = OwnSessionId,
                Role = "user",
                Content = "Hello there",
                CreatedAt = baseline.AddMinutes(1)
            },
            new ChatMessage
            {
                ChatSessionId = OwnSessionId,
                Role = "assistant",
                Content = "Hi! How can I help?",
                CreatedAt = baseline.AddMinutes(2)
            });
        db.SaveChanges();
    }

    /// <summary>SHA-256 hex digest — the only representation of a token that is stored.</summary>
    private static string HashOf(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Reads a JSON timestamp as the UTC instant it denotes.
    ///
    /// <para>The Kind-less branch is a TEST-HOST artifact, not a product defect. These
    /// tests run on SQLite, which has no timestamp type and hands EF back a
    /// <c>DateTimeKind.Unspecified</c> value, so a deadline written as UTC is serialized
    /// with no <c>Z</c>; calling <c>ToUniversalTime()</c> on that would shift it by the
    /// machine's offset and make the assertion depend on where the build runs. In
    /// production the column is PostgreSQL <c>timestamp with time zone</c> and Npgsql
    /// round-trips <c>Kind.Utc</c> intact, which is the other branch.</para>
    /// </summary>
    private static DateTime AsUtc(JsonElement element)
    {
        var value = element.GetDateTime();
        return value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
    }

    /// <summary>
    /// Writes a share link straight to the database in an exact, chosen state, and
    /// returns the raw token that resolves to it.
    ///
    /// <para>Deliberately not "mint through the endpoint, then edit the row". Two
    /// reasons. Minting needs a login, and <c>LoginPolicy</c> allows five per ten
    /// seconds per IP — the pre-existing tests already use four, so extra logins here
    /// would make the class fail on the rate limiter instead of on its assertions.
    /// Minting also REVOKES every other active link on the same session, which is
    /// precisely the state these tests must control. Seeding gives one link, in one
    /// state, with nothing else touching it.</para>
    ///
    /// <para>Everything lands on <see cref="SpareSessionId"/> so these rows can never
    /// disturb the <c>SingleAsync</c> the create test runs over
    /// <see cref="OwnSessionId"/>, whatever order xUnit picks.</para>
    /// </summary>
    private async Task<string> SeedLinkAsync(DateTime expiresAtUtc, DateTime? revokedAtUtc = null)
    {
        var rawToken = $"seeded-{Guid.NewGuid():N}";
        await EnsureSpareSessionAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        db.ChatShareLinks.Add(new ChatShareLink
        {
            ChatSessionId = SpareSessionId,
            TenantId = LmKitApiFactory.TenantId,
            TokenHash = HashOf(rawToken),
            CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
            RevokedAtUtc = revokedAtUtc,
            ExpiresAtUtc = expiresAtUtc
        });
        await db.SaveChangesAsync();
        return rawToken;
    }

    /// <summary>
    /// A second owned session with a one-message transcript, created once. Keeps seeded
    /// links off <see cref="OwnSessionId"/>, whose link state the pre-existing tests own.
    /// </summary>
    private async Task EnsureSpareSessionAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        if (await db.ChatSessions.AnyAsync(session => session.Id == SpareSessionId)) return;

        db.ChatSessions.Add(new ChatSession
        {
            Id = SpareSessionId,
            TenantId = LmKitApiFactory.TenantId,
            UserId = LmKitApiFactory.UserId,
            Title = "Spare session"
        });
        db.ChatMessages.Add(new ChatMessage
        {
            ChatSessionId = SpareSessionId,
            Role = "user",
            Content = "Seeded transcript",
            CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();
    }

    private static async Task<string> ReadTokenAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        return token!;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = LmKitApiFactory.Email,
            password = LmKitApiFactory.Password
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }
}

/// <summary>
/// The lifetime knob itself. Small surface, but it is the one place where a
/// configuration value can widen the exposure window, so its bounds are pinned.
/// </summary>
public sealed class ShareLinkOptionsTests
{
    /// <summary>
    /// The option and the entity constant must agree. They are two halves of the same
    /// policy — the entity's initializer is what a creation site gets if it never reads
    /// the option — and a silent divergence would mean links live for a length nobody
    /// wrote down.
    /// </summary>
    [Fact]
    public void Default_MatchesTheEntityFallback()
    {
        Assert.Equal(ChatShareLink.DefaultTimeToLiveDays, new ShareLinkOptions().TimeToLiveDays);
        Assert.Equal(TimeSpan.FromDays(ChatShareLink.DefaultTimeToLiveDays), new ShareLinkOptions().TimeToLive);
    }

    /// <summary>
    /// A typo in configuration must not be able to mint a link that is already dead, nor
    /// one that is permanent by arithmetic. Clamped rather than thrown: refusing to start
    /// the API over a share-link setting would be a worse failure than ignoring it.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-30, 1)]
    [InlineData(int.MinValue, 1)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    [InlineData(365, 365)]
    [InlineData(100_000, 365)]
    [InlineData(int.MaxValue, 365)]
    public void TimeToLive_IsClampedToASaneWindow(int configured, int expectedDays)
    {
        var options = new ShareLinkOptions { TimeToLiveDays = configured };

        Assert.Equal(TimeSpan.FromDays(expectedDays), options.TimeToLive);
    }
}
