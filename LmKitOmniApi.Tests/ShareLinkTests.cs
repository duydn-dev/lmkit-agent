using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private readonly LmKitApiFactory _factory;

    public ShareLinkTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
        EnsureConversationSeeded();
    }

    [Fact]
    public async Task CreateShareLink_ThenAnonymousGet_ReturnsTranscriptWithoutSystemMessages()
    {
        using var owner = await CreateAuthenticatedClientAsync();
        using var anonymous = _factory.CreateClient();

        var created = await owner.PostAsync($"/api/share/chat-sessions/{OwnSessionId}", null);
        var token = await ReadTokenAsync(created);
        var shared = await anonymous.GetAsync($"/api/share/chat/{token}");
        var payload = await shared.Content.ReadFromJsonAsync<JsonElement>();

        // Raw token: 32 random bytes, base64url without padding → exactly 43 URL-safe chars.
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token);

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

        // Persistence stores only the SHA-256 hex digest — never the raw token.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        var storedHash = await db.ChatShareLinks.AsNoTracking()
            .Where(link => link.ChatSessionId == OwnSessionId && link.RevokedAtUtc == null)
            .Select(link => link.TokenHash)
            .SingleAsync();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), storedHash);
        Assert.NotEqual(token, storedHash);
    }

    [Fact]
    public async Task RevokeShareLinks_MakesExistingTokenReturnNotFound()
    {
        using var owner = await CreateAuthenticatedClientAsync();
        using var anonymous = _factory.CreateClient();

        var created = await owner.PostAsync($"/api/share/chat-sessions/{OwnSessionId}", null);
        var token = await ReadTokenAsync(created);
        var beforeRevoke = await anonymous.GetAsync($"/api/share/chat/{token}");
        var revoke = await owner.DeleteAsync($"/api/share/chat-sessions/{OwnSessionId}");
        var afterRevoke = await anonymous.GetAsync($"/api/share/chat/{token}");

        Assert.Equal(HttpStatusCode.OK, beforeRevoke.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        // Revoked and unknown tokens must be indistinguishable: both are a plain 404.
        Assert.Equal(HttpStatusCode.NotFound, afterRevoke.StatusCode);
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
        Assert.Equal(HttpStatusCode.NotFound, oldTokenResponse.StatusCode);
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
