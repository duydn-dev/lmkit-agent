using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Contract tests for POST /api/vision/upload — the image entry point that makes
/// the vision-tools screen usable. Covers the security gates (extension
/// allow-list, magic-byte signature) and the happy path returning an owned path.
/// No VLM inference is exercised here; only the upload/validation contract.
/// </summary>
public sealed class VisionUploadApiTests : IClassFixture<LmKitApiFactory>
{
    // First eight bytes are the canonical PNG signature the validator checks.
    private static readonly byte[] PngBytes =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01
    };

    private static readonly SemaphoreSlim ClientGate = new(1, 1);
    private static HttpClient? _ownerClient;

    private readonly LmKitApiFactory _factory;

    public VisionUploadApiTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    [Fact]
    public async Task Upload_ValidPng_ReturnsOwnedImagePath()
    {
        var client = await OwnerClientAsync();

        using var form = BuildImageForm(PngBytes, "photo.png", "image/png");
        var response = await client.PostAsync("/api/vision/upload", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var imagePath = body.GetProperty("imagePath").GetString();
        Assert.False(string.IsNullOrWhiteSpace(imagePath));
        // Stored under the caller's isolated upload root, with a server-chosen name.
        Assert.Contains("Uploads", imagePath!);
        Assert.EndsWith(".png", body.GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task Upload_RejectsNonImageExtension()
    {
        var client = await OwnerClientAsync();

        using var form = BuildImageForm(Encoding.UTF8.GetBytes("just some text"), "notes.txt", "text/plain");
        var response = await client.PostAsync("/api/vision/upload", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_RejectsContentThatDoesNotMatchExtension()
    {
        var client = await OwnerClientAsync();

        // A .png name but the bytes are not a PNG → signature check must reject it.
        using var form = BuildImageForm(Encoding.UTF8.GetBytes("this is definitely not a png header"), "fake.png", "image/png");
        var response = await client.PostAsync("/api/vision/upload", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_RejectsAnonymousCallers()
    {
        using var anonymous = _factory.CreateClient();

        using var form = BuildImageForm(PngBytes, "photo.png", "image/png");
        var response = await anonymous.PostAsync("/api/vision/upload", form);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static MultipartFormDataContent BuildImageForm(byte[] bytes, string fileName, string contentType)
    {
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var form = new MultipartFormDataContent
        {
            { fileContent, "image", fileName }
        };
        return form;
    }

    private async Task<HttpClient> OwnerClientAsync()
    {
        await ClientGate.WaitAsync();
        try { return _ownerClient ??= await LoginAsync(LmKitApiFactory.Email, LmKitApiFactory.Password); }
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
