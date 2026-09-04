using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Contract tests for temporary ("Chat tạm thời") chat sessions: creating one marks
/// IsEphemeral and hides it from the chat list AND search, while an ordinary session
/// keeps appearing. Deterministic and model-free — no chat streaming is invoked (that
/// would require a model); the persistence-skip is a documented handler behavior, and
/// the user-facing contract exercised here is the list/search exclusion. Logins are
/// cached per class to stay under the login rate limiter, mirroring ProjectApiTests.
/// </summary>
public sealed class EphemeralChatTests : IClassFixture<LmKitApiFactory>
{
    private static readonly SemaphoreSlim ClientGate = new(1, 1);
    private static HttpClient? _ownerClient;
    private readonly LmKitApiFactory _factory;

    public EphemeralChatTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    [Fact]
    public async Task CreateEphemeralSession_MarksIsEphemeral_AndIsHiddenFromListAndSearch()
    {
        var client = await CreateAuthenticatedClientAsync();

        // A normal session appears in the list; an ephemeral one does not.
        var normal = await client.PostAsync("/api/chat/sessions", null);
        var normalBody = await ReadJsonAsync(normal, HttpStatusCode.OK);
        var normalId = normalBody.GetProperty("id").GetGuid();
        Assert.False(normalBody.GetProperty("isEphemeral").GetBoolean());

        var ephemeral = await client.PostAsJsonAsync("/api/chat/sessions", new { ephemeral = true });
        var ephemeralBody = await ReadJsonAsync(ephemeral, HttpStatusCode.OK);
        var ephemeralId = ephemeralBody.GetProperty("id").GetGuid();
        Assert.True(ephemeralBody.GetProperty("isEphemeral").GetBoolean());

        // The main list contains the normal session but never the ephemeral one.
        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/chat/sessions");
        Assert.Contains(list!, e => e.GetProperty("id").GetGuid() == normalId);
        Assert.DoesNotContain(list!, e => e.GetProperty("id").GetGuid() == ephemeralId);

        // Search (empty query = full list, and a term) also excludes the ephemeral session.
        var search = await client.GetFromJsonAsync<JsonElement[]>("/api/chat/sessions/search?q=");
        Assert.Contains(search!, e => e.GetProperty("id").GetGuid() == normalId);
        Assert.DoesNotContain(search!, e => e.GetProperty("id").GetGuid() == ephemeralId);

        // The stored rows carry the expected flag.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        Assert.True(await db.ChatSessions.AsNoTracking().Where(s => s.Id == ephemeralId).Select(s => s.IsEphemeral).SingleAsync());
        Assert.False(await db.ChatSessions.AsNoTracking().Where(s => s.Id == normalId).Select(s => s.IsEphemeral).SingleAsync());
    }

    [Fact]
    public async Task EphemeralSessionWithMessages_IsExcludedFromList_WhileNormalWithMessagesAppears()
    {
        var client = await CreateAuthenticatedClientAsync();

        var ephemeralId = Guid.NewGuid();
        var normalId = Guid.NewGuid();

        // Seed both sessions WITH persisted messages directly, so the test proves the
        // list exclusion is driven purely by IsEphemeral — not by "has no messages".
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
            db.ChatSessions.AddRange(
                new ChatSession { Id = ephemeralId, TenantId = LmKitApiFactory.TenantId, UserId = LmKitApiFactory.UserId, Title = "Tạm thời", IsEphemeral = true },
                new ChatSession { Id = normalId, TenantId = LmKitApiFactory.TenantId, UserId = LmKitApiFactory.UserId, Title = "Bình thường", IsEphemeral = false });
            db.ChatMessages.AddRange(
                new ChatMessage { ChatSessionId = ephemeralId, Role = "user", Content = "xin chào tạm thời" },
                new ChatMessage { ChatSessionId = normalId, Role = "user", Content = "xin chào bình thường" });
            await db.SaveChangesAsync();
        }

        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/chat/sessions");
        Assert.DoesNotContain(list!, e => e.GetProperty("id").GetGuid() == ephemeralId);
        Assert.Contains(list!, e => e.GetProperty("id").GetGuid() == normalId);

        // The ephemeral session's messages still exist in the DB (exclusion is list-only).
        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<HermesDbContext>();
        Assert.Equal(1, await verifyDb.ChatMessages.AsNoTracking().CountAsync(m => m.ChatSessionId == ephemeralId));
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        await ClientGate.WaitAsync();
        try
        {
            return _ownerClient ??= await LoginAsync(LmKitApiFactory.Email, LmKitApiFactory.Password);
        }
        finally
        {
            ClientGate.Release();
        }
    }

    private async Task<HttpClient> LoginAsync(string email, string password)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }
}
