using LmKitOmniApi.Application.Speech.Commands;
using LmKitOmniApi.Application.Speech.Handlers;
using LmKitOmniApi.Infrastructure.AI.Voice;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Unit tests for <see cref="SynthesizeSpeechCommandHandler"/> — the off-by-default TTS handler.
/// Proves the "not configured" gates (disabled, no engine, unavailable engine), the success path
/// when a real engine is plugged in via the <see cref="ISpeechSynthesizer"/> seam, and input
/// validation. No model or audio is needed because LM-Kit ships no TTS engine — the seam is faked.
/// </summary>
public sealed class VoiceSynthesisHandlerTests
{
    [Fact]
    public async Task Synthesize_WhenTtsDisabled_ReturnsNotConfigured()
    {
        var handler = new SynthesizeSpeechCommandHandler(
            Options.Create(new VoiceOptions { TtsEnabled = false }),
            synthesizer: new FakeSynthesizer { IsAvailable = true });

        var result = await handler.Handle(new SynthesizeSpeechCommand { Text = "hello" }, CancellationToken.None);

        Assert.Equal(SynthesizeSpeechStatus.EngineNotConfigured, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Empty(result.Audio);
    }

    [Fact]
    public async Task Synthesize_WhenEnabledButNoEngineRegistered_ReturnsNotConfigured()
    {
        // This mirrors the real DI wiring: TtsEnabled=true but no ISpeechSynthesizer registered
        // (LM-Kit has none), so the optional dependency is null.
        var handler = new SynthesizeSpeechCommandHandler(
            Options.Create(new VoiceOptions { TtsEnabled = true }),
            synthesizer: null);

        var result = await handler.Handle(new SynthesizeSpeechCommand { Text = "hello" }, CancellationToken.None);

        Assert.Equal(SynthesizeSpeechStatus.EngineNotConfigured, result.Status);
    }

    [Fact]
    public async Task Synthesize_WhenEngineUnavailable_ReturnsNotConfigured()
    {
        var handler = new SynthesizeSpeechCommandHandler(
            Options.Create(new VoiceOptions { TtsEnabled = true }),
            synthesizer: new FakeSynthesizer { IsAvailable = false });

        var result = await handler.Handle(new SynthesizeSpeechCommand { Text = "hello" }, CancellationToken.None);

        Assert.Equal(SynthesizeSpeechStatus.EngineNotConfigured, result.Status);
    }

    [Fact]
    public async Task Synthesize_WhenEnabledWithEngine_ReturnsAudioBytes()
    {
        var synthesizer = new FakeSynthesizer { IsAvailable = true, Output = new byte[] { 10, 20, 30, 40 } };
        var handler = new SynthesizeSpeechCommandHandler(
            Options.Create(new VoiceOptions { TtsEnabled = true, DefaultVoice = "default" }),
            synthesizer);

        var result = await handler.Handle(
            new SynthesizeSpeechCommand { Text = "hello", Voice = "alto" }, CancellationToken.None);

        Assert.Equal(SynthesizeSpeechStatus.Success, result.Status);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, result.Audio);
        Assert.Equal("audio/wav", result.ContentType);
        Assert.Equal("alto", synthesizer.LastVoice);
    }

    [Fact]
    public async Task Synthesize_WhenVoiceOmitted_UsesConfiguredDefaultVoice()
    {
        var synthesizer = new FakeSynthesizer { IsAvailable = true };
        var handler = new SynthesizeSpeechCommandHandler(
            Options.Create(new VoiceOptions { TtsEnabled = true, DefaultVoice = "storyteller" }),
            synthesizer);

        await handler.Handle(new SynthesizeSpeechCommand { Text = "hello", Voice = null }, CancellationToken.None);

        Assert.Equal("storyteller", synthesizer.LastVoice);
    }

    [Fact]
    public async Task Synthesize_WhenTextEmpty_Throws()
    {
        var handler = new SynthesizeSpeechCommandHandler(
            Options.Create(new VoiceOptions { TtsEnabled = true }),
            new FakeSynthesizer { IsAvailable = true });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.Handle(new SynthesizeSpeechCommand { Text = "   " }, CancellationToken.None));
    }

    private sealed class FakeSynthesizer : ISpeechSynthesizer
    {
        public bool IsAvailable { get; set; } = true;
        public byte[] Output { get; set; } = new byte[] { 1 };
        public string? LastVoice { get; private set; }

        public Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
        {
            LastVoice = voice;
            return Task.FromResult(Output);
        }
    }
}
