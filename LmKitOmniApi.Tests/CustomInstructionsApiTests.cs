using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Contract tests for the user-level custom-instructions endpoint
/// (GET/PUT /api/user/custom-instructions). Deterministic and model-free: verifies the
/// GET/PUT round-trip, in-place update, clearing, length validation, anonymous
/// rejection, and — crucially — that the row is strictly self-scoped so another
/// tenant/user can neither read nor overwrite it. Logins are cached per class to stay
/// under the login rate limiter, mirroring ProjectApiTests.
/// </summary>
public sealed class CustomInstructionsApiTests : IClassFixture<LmKitApiFactory>
{
    private const string Endpoint = "/api/user/custom-instructions";

    private static readonly SemaphoreSlim ClientGate = new(1, 1);
    private static HttpClient? _ownerClient;
    private static HttpClient? _otherTenantClient;
    private readonly LmKitApiFactory _factory;

    public CustomInstructionsApiTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    [Fact]
    public async Task Put_ThenGet_RoundTripsAndUpdatesInPlace()
    {
        var client = await CreateAuthenticatedClientAsync();

        var save = await client.PutAsJsonAsync(Endpoint, new
        {
            aboutUser = "  Tôi là kỹ sư phần mềm ở Hà Nội.  ",
            responseStyle = "Trả lời ngắn gọn, dùng tiếng Việt."
        });
        var saved = await ReadJsonAsync(save, HttpStatusCode.OK);
        // Stored trimmed.
        Assert.Equal("Tôi là kỹ sư phần mềm ở Hà Nội.", saved.GetProperty("aboutUser").GetString());
        Assert.Equal("Trả lời ngắn gọn, dùng tiếng Việt.", saved.GetProperty("responseStyle").GetString());
        Assert.True(saved.GetProperty("updatedAtUtc").GetDateTime() > DateTime.UtcNow.AddMinutes(-5));

        var fetched = await client.GetFromJsonAsync<JsonElement>(Endpoint);
        Assert.Equal("Tôi là kỹ sư phần mềm ở Hà Nội.", fetched.GetProperty("aboutUser").GetString());
        Assert.Equal("Trả lời ngắn gọn, dùng tiếng Việt.", fetched.GetProperty("responseStyle").GetString());

        // A second PUT replaces in place (still one row, new values).
        var update = await client.PutAsJsonAsync(Endpoint, new { aboutUser = "Đã cập nhật.", responseStyle = (string?)null });
        var updated = await ReadJsonAsync(update, HttpStatusCode.OK);
        Assert.Equal("Đã cập nhật.", updated.GetProperty("aboutUser").GetString());
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("responseStyle").ValueKind);

        var refetched = await client.GetFromJsonAsync<JsonElement>(Endpoint);
        Assert.Equal("Đã cập nhật.", refetched.GetProperty("aboutUser").GetString());
        Assert.Equal(JsonValueKind.Null, refetched.GetProperty("responseStyle").ValueKind);
    }

    [Fact]
    public async Task Put_WithWhitespaceOrEmptyObject_ClearsFields()
    {
        var client = await CreateAuthenticatedClientAsync();

        await client.PutAsJsonAsync(Endpoint, new { aboutUser = "tạm", responseStyle = "tạm" });

        // Whitespace collapses to null; an empty object clears both.
        var cleared = await ReadJsonAsync(await client.PutAsJsonAsync(Endpoint, new { aboutUser = "   ", responseStyle = "\t" }), HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("aboutUser").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("responseStyle").ValueKind);

        var emptied = await ReadJsonAsync(await client.PutAsJsonAsync(Endpoint, new { }), HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, emptied.GetProperty("aboutUser").ValueKind);
        Assert.Equal(JsonValueKind.Null, emptied.GetProperty("responseStyle").ValueKind);
    }

    [Fact]
    public async Task Instructions_AreStrictlySelfScoped_AcrossUsersAndTenants()
    {
        var owner = await CreateAuthenticatedClientAsync();
        var stranger = await CreateOtherTenantClientAsync();

        await owner.PutAsJsonAsync(Endpoint, new { aboutUser = "Bí mật của chủ sở hữu.", responseStyle = "Giọng của chủ sở hữu." });

        // The stranger cannot READ the owner's instructions — they only ever see their own.
        var strangerBefore = await stranger.GetFromJsonAsync<JsonElement>(Endpoint);
        Assert.NotEqual("Bí mật của chủ sở hữu.", strangerBefore.GetProperty("aboutUser").GetString());

        // The stranger writes their own; this must NOT touch the owner's row.
        await stranger.PutAsJsonAsync(Endpoint, new { aboutUser = "Của người lạ.", responseStyle = "Của người lạ." });

        var ownerAfter = await owner.GetFromJsonAsync<JsonElement>(Endpoint);
        Assert.Equal("Bí mật của chủ sở hữu.", ownerAfter.GetProperty("aboutUser").GetString());
        Assert.Equal("Giọng của chủ sở hữu.", ownerAfter.GetProperty("responseStyle").GetString());

        var strangerAfter = await stranger.GetFromJsonAsync<JsonElement>(Endpoint);
        Assert.Equal("Của người lạ.", strangerAfter.GetProperty("aboutUser").GetString());
    }

    [Fact]
    public async Task Put_RejectsOverlongFieldsWithVietnameseMessages()
    {
        var client = await CreateAuthenticatedClientAsync();

        var longAbout = await client.PutAsJsonAsync(Endpoint, new { aboutUser = new string('a', 2001) });
        var longStyle = await client.PutAsJsonAsync(Endpoint, new { responseStyle = new string('b', 2001) });

        Assert.Equal(HttpStatusCode.BadRequest, longAbout.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longStyle.StatusCode);
        Assert.Equal("Thông tin về bạn không được vượt quá 2000 ký tự.", await ReadMessageAsync(longAbout));
        Assert.Equal("Phong cách phản hồi không được vượt quá 2000 ký tự.", await ReadMessageAsync(longStyle));

        // Exactly at the limit is accepted.
        var atLimit = await client.PutAsJsonAsync(Endpoint, new { aboutUser = new string('a', 2000) });
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
    }

    [Fact]
    public async Task Endpoints_RejectAnonymousCallers()
    {
        using var anonymous = _factory.CreateClient();

        var get = await anonymous.GetAsync(Endpoint);
        var put = await anonymous.PutAsJsonAsync(Endpoint, new { aboutUser = "x" });

        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string?> ReadMessageAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("message").GetString();
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

    private async Task<HttpClient> CreateOtherTenantClientAsync()
    {
        await ClientGate.WaitAsync();
        try
        {
            return _otherTenantClient ??= await LoginAsync("other@example.test", "Other-2026!");
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
