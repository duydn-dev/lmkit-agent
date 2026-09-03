using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LmKitOmniApi.Application.Speech.Commands;
using LmKitOmniApi.Infrastructure.AI.Voice;
using MediatR;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Contract tests for the voice endpoints on SpeechController and their DI wiring. Covers only
/// what CI can verify without a model or audio hardware: the off-by-default synthesize contract
/// (501), input validation, endpoint authentication, and that the command/handler graph resolves
/// from DI. The actual TTS audio, streaming decode, and LiveKit media loop are live-only.
/// </summary>
public sealed class VoiceSpeechApiTests : IClassFixture<LmKitApiFactory>
{
    private readonly LmKitApiFactory _factory;

    public VoiceSpeechApiTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    [Fact]
    public async Task Synthesize_WhenTtsDisabled_Returns501NotConfigured()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/speech/synthesize", new { text = "Xin chào" });

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Synthesize_WithEmptyText_Returns400()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/speech/synthesize", new { text = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Synthesize_RejectsAnonymousCallers()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/api/speech/synthesize", new { text = "hello" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TranscribeStream_RejectsAnonymousCallers()
    {
        using var anonymous = _factory.CreateClient();

        using var form = new MultipartFormDataContent { { new StringContent("x"), "dummy" } };
        var response = await anonymous.PostAsync("/api/speech/transcribe-stream", form);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TranscribeStream_WithoutAudioField_Returns400()
    {
        using var client = await CreateAuthenticatedClientAsync();

        using var form = new MultipartFormDataContent { { new StringContent("x"), "dummy" } };
        var response = await client.PostAsync("/api/speech/transcribe-stream", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TranscribeStream_WithUnsupportedFormat_Returns400()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("not audio"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        using var form = new MultipartFormDataContent { { fileContent, "audio", "notes.txt" } };

        var response = await client.PostAsync("/api/speech/transcribe-stream", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SpeechToken_RejectsAnonymousCallers()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/api/speech/token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SpeechToken_WhenLiveKitUnconfigured_Returns500ForAuthenticatedCaller()
    {
        using var client = await CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/speech/token");

        // LiveKit ApiKey/Secret are empty in the test config, so the endpoint reports 500 —
        // proving the caller was authenticated and reached the action.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public void VoiceServices_ResolveFromDependencyInjection()
    {
        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider;

        // MediatR-registered handlers for the frozen contract resolve.
        Assert.NotNull(provider.GetService<IRequestHandler<SynthesizeSpeechCommand, SynthesizeSpeechResult>>());
        Assert.NotNull(provider.GetService<IStreamRequestHandler<TranscribeAudioStreamCommand, TranscriptionPartial>>());

        // VoiceOptions is bound and off by default.
        var options = provider.GetRequiredService<IOptions<VoiceOptions>>().Value;
        Assert.False(options.TtsEnabled);
        Assert.False(options.LiveAgentEnabled);

        // No TTS engine is registered by default — the honest "no engine" state.
        Assert.Null(provider.GetService<ISpeechSynthesizer>());
    }

    private Task<HttpClient> CreateAuthenticatedClientAsync() => CreateAuthenticatedClientAsync(_factory);

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(LmKitApiFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
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
