using System.Net;
using System.Net.Http.Json;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Contract tests for GET /api/files/{id} — the owner-scoped, cookie-authenticated
/// endpoint that serves interpreter-produced files back to the chat UI. Proves the
/// happy path, that an id is resolved ONLY inside the caller's own upload dir (so an
/// identical id yields a different — and here missing — file for another user),
/// that path traversal is defused, and that anonymous callers are refused.
/// </summary>
public sealed class FilesControllerTests : IClassFixture<LmKitApiFactory>
{
    private static readonly SemaphoreSlim ClientGate = new(1, 1);
    private static HttpClient? _ownerClient;
    private static HttpClient? _otherTenantClient;

    private readonly LmKitApiFactory _factory;

    public FilesControllerTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    [Fact]
    public async Task Download_ServesAnOwnedFile_WithItsContentType()
    {
        var storedName = $"{Guid.NewGuid():N}.png";
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
        WriteOwnedFile(LmKitApiFactory.TenantId, LmKitApiFactory.UserId, storedName, bytes);

        try
        {
            var client = await OwnerClientAsync();
            var response = await client.GetAsync($"/api/files/{storedName}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            DeleteOwnedFile(LmKitApiFactory.TenantId, LmKitApiFactory.UserId, storedName);
        }
    }

    [Fact]
    public async Task Download_ResolvesWithinTheCallersOwnDirectory_SoAnotherUserGets404()
    {
        // Same id, owned only by the first user: the second user's request resolves
        // under THEIR upload root, where the file does not exist → 404 (never leaks).
        var storedName = $"{Guid.NewGuid():N}.txt";
        WriteOwnedFile(LmKitApiFactory.TenantId, LmKitApiFactory.UserId, storedName, new byte[] { 1, 2, 3 });

        try
        {
            var owner = await OwnerClientAsync();
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/files/{storedName}")).StatusCode);

            var stranger = await OtherTenantClientAsync();
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/files/{storedName}")).StatusCode);
        }
        finally
        {
            DeleteOwnedFile(LmKitApiFactory.TenantId, LmKitApiFactory.UserId, storedName);
        }
    }

    [Fact]
    public async Task Download_MissingFile_Returns404()
    {
        var client = await OwnerClientAsync();
        var response = await client.GetAsync($"/api/files/{Guid.NewGuid():N}.png");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_PathTraversalId_IsDefused()
    {
        // Encoded traversal: the id is collapsed to a bare file name, so it can never
        // escape the upload root. Result is a benign 404 (or 400), never a served file.
        var client = await OwnerClientAsync();
        var response = await client.GetAsync("/api/files/..%2F..%2Fappsettings.json");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Download_RejectsAnonymousCallers()
    {
        using var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync($"/api/files/{Guid.NewGuid():N}.png");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private void WriteOwnedFile(Guid tenantId, Guid userId, string name, byte[] bytes)
    {
        using var scope = _factory.Services.CreateScope();
        var resources = scope.ServiceProvider.GetRequiredService<UserResourceAccessService>();
        var dir = resources.GetUploadDirectory(tenantId, userId);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), bytes);
    }

    private void DeleteOwnedFile(Guid tenantId, Guid userId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var resources = scope.ServiceProvider.GetRequiredService<UserResourceAccessService>();
        var path = Path.Combine(resources.GetUploadDirectory(tenantId, userId), name);
        if (File.Exists(path)) File.Delete(path);
    }

    private async Task<HttpClient> OwnerClientAsync()
    {
        await ClientGate.WaitAsync();
        try { return _ownerClient ??= await LoginAsync(LmKitApiFactory.Email, LmKitApiFactory.Password); }
        finally { ClientGate.Release(); }
    }

    private async Task<HttpClient> OtherTenantClientAsync()
    {
        await ClientGate.WaitAsync();
        try { return _otherTenantClient ??= await LoginAsync("other@example.test", "Other-2026!"); }
        finally { ClientGate.Release(); }
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
