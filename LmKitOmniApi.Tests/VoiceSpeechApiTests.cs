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

        // The Piper TTS engine is registered but OFF by default: with TtsEnabled=false and no
        // binary/model configured it reports IsAvailable=false, so the synthesize endpoint
        // still answers 501 (honest "not configured" state, no fake audio).
        var synth = provider.GetService<ISpeechSynthesizer>();
        Assert.NotNull(synth);
        Assert.False(synth!.IsAvailable);
    }

    /// <summary>
    /// The SHIPPED configuration must agree with the code defaults, knob for knob.
    ///
    /// This is not belt-and-braces: a value written into appsettings.json silently overrides a
    /// new code default, and a fix whose default never takes effect is a fix that does nothing.
    /// Anything added to the "Voice" block that disagrees with <see cref="VoiceOptions"/>'s own
    /// initializers fails here rather than in production.
    /// </summary>
    [Fact]
    public void VoiceDispatcherOptions_AsShipped_MatchTheCodeDefaults_AndAreOff()
    {
        using var scope = _factory.Services.CreateScope();
        var bound = scope.ServiceProvider.GetRequiredService<IOptions<VoiceOptions>>().Value;
        var code = new VoiceOptions();

        // Off by default, both switches — the single-room agent is what ships.
        Assert.False(bound.LiveAgentEnabled);
        Assert.False(bound.DispatcherEnabled);
        Assert.False(bound.DispatcherActive);

        Assert.Equal(code.MaxConcurrentRooms, bound.MaxConcurrentRooms);
        Assert.Equal(code.MaxRoomsPerTenant, bound.MaxRoomsPerTenant);
        Assert.Equal(code.MaxConcurrentTurns, bound.MaxConcurrentTurns);
        Assert.Equal(code.RoomIdleTimeoutSeconds, bound.RoomIdleTimeoutSeconds);
        Assert.Equal(code.TurnBudgetSeconds, bound.TurnBudgetSeconds);
        Assert.Equal(code.DispatcherPollSeconds, bound.DispatcherPollSeconds);
        Assert.Equal(code.RoomRejoinDelaySeconds, bound.RoomRejoinDelaySeconds);
        Assert.Equal(code.DispatcherShutdownDrainSeconds, bound.DispatcherShutdownDrainSeconds);
        Assert.Equal(code.AgentConsentTtlMinutes, bound.AgentConsentTtlMinutes);
        Assert.Equal(code.MaxAgentConsentGrants, bound.MaxAgentConsentGrants);
        Assert.Equal(code.MaxAgentConsentGrantsPerUser, bound.MaxAgentConsentGrantsPerUser);

        // And the shipped defaults are internally consistent, so the dispatcher would not stand
        // down on its own configuration the moment an operator turns it on.
        Assert.True(bound.TryValidateDispatcher(out var error), error);
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
