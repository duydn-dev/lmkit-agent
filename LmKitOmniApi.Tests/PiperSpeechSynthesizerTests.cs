using System.Text;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.AI.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The local Piper TTS engine. Docker/binary-free: a fake IProcessRunner stands in for the
/// Piper CLI (writing WAV bytes to the --output_file it is handed), so the safety/wiring —
/// availability gating, argument construction, stdin text, reading + cleaning up the output,
/// and failure handling — is verified in CI. Real Piper execution needs the binary + a voice
/// model on the host and is exercised there only.
/// </summary>
public sealed class PiperSpeechSynthesizerTests : IDisposable
{
    private readonly string _modelPath;
    private static readonly byte[] FakeWav = Encoding.ASCII.GetBytes("RIFFfake-wav-bytes");

    public PiperSpeechSynthesizerTests()
    {
        _modelPath = Path.Combine(Path.GetTempPath(), $"piper-model-{Guid.NewGuid():N}.onnx");
        File.WriteAllText(_modelPath, "onnx-placeholder");
    }

    [Fact]
    public void IsAvailable_False_WhenDisabled()
    {
        var s = Create(new VoiceOptions { TtsEnabled = false, PiperExecutablePath = "piper", PiperVoices = { ["default"] = _modelPath } }, Ok());
        Assert.False(s.IsAvailable);
    }

    [Fact]
    public void IsAvailable_False_WhenExecutableMissing_OrNoModel()
    {
        Assert.False(Create(new VoiceOptions { TtsEnabled = true, PiperExecutablePath = "", PiperVoices = { ["default"] = _modelPath } }, Ok()).IsAvailable);
        Assert.False(Create(new VoiceOptions { TtsEnabled = true, PiperExecutablePath = "piper" }, Ok()).IsAvailable); // no voices
        Assert.False(Create(new VoiceOptions { TtsEnabled = true, PiperExecutablePath = "piper", PiperVoices = { ["default"] = "C:/nope/missing.onnx" } }, Ok()).IsAvailable);
    }

    [Fact]
    public void IsAvailable_True_WhenEnabledAndConfiguredWithExistingModel()
    {
        var s = Create(new VoiceOptions { TtsEnabled = true, PiperExecutablePath = "piper", PiperVoices = { ["default"] = _modelPath } }, Ok());
        Assert.True(s.IsAvailable);
    }

    [Fact]
    public async Task Synthesize_BuildsPiperArgs_FeedsTextOnStdin_AndReturnsTheWav()
    {
        var runner = Ok();
        var s = Create(new VoiceOptions { TtsEnabled = true, PiperExecutablePath = "/opt/piper/piper", PiperVoices = { ["vi"] = _modelPath } }, runner);

        var audio = await s.SynthesizeAsync("Xin chào", "vi", CancellationToken.None);

        Assert.Equal(FakeWav, audio);
        Assert.Equal("/opt/piper/piper", runner.FileName);
        Assert.Equal("Xin chào", runner.Stdin);
        Assert.Contains("--model", runner.Args!);
        Assert.Contains(_modelPath, runner.Args!);
        Assert.Contains("--output_file", runner.Args!);
        // The temp output file is cleaned up after the bytes are read.
        var outIdx = runner.Args!.ToList().IndexOf("--output_file");
        Assert.False(File.Exists(runner.Args![outIdx + 1]));
    }

    [Fact]
    public async Task Synthesize_ResolvesVoice_ExactThenDefaultThenAny()
    {
        var runner = Ok();
        var s = Create(new VoiceOptions { TtsEnabled = true, PiperExecutablePath = "piper", DefaultVoice = "default", PiperVoices = { ["default"] = _modelPath } }, runner);

        // Unknown voice → falls back to the default voice's model.
        await s.SynthesizeAsync("hi", "does-not-exist", CancellationToken.None);
        Assert.Contains(_modelPath, runner.Args!);
    }

    [Fact]
    public async Task Synthesize_Throws_OnNonZeroExit()
    {
        var runner = new FakeProcessRunner(_ => new ProcessRunResult(1, "", "piper: bad model", false));
        var s = Create(new VoiceOptions { TtsEnabled = true, PiperExecutablePath = "piper", PiperVoices = { ["default"] = _modelPath } }, runner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.SynthesizeAsync("hi", "default", CancellationToken.None));
    }

    [Fact]
    public async Task Synthesize_Throws_OnTimeout()
    {
        var runner = new FakeProcessRunner(_ => new ProcessRunResult(0, "", "", true));
        var s = Create(new VoiceOptions { TtsEnabled = true, PiperExecutablePath = "piper", PiperVoices = { ["default"] = _modelPath } }, runner);
        await Assert.ThrowsAsync<TimeoutException>(() => s.SynthesizeAsync("hi", "default", CancellationToken.None));
    }

    [Fact]
    public async Task Synthesize_Throws_WhenNoModelConfigured()
    {
        var s = Create(new VoiceOptions { TtsEnabled = true, PiperExecutablePath = "piper" }, Ok());
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.SynthesizeAsync("hi", "default", CancellationToken.None));
    }

    // ── helpers ──

    private static PiperSpeechSynthesizer Create(VoiceOptions options, IProcessRunner runner) =>
        new(runner, Options.Create(options), NullLogger<PiperSpeechSynthesizer>.Instance);

    /// <summary>A runner that simulates Piper: writes the fake WAV to the --output_file path, exit 0.</summary>
    private static FakeProcessRunner Ok() => new(args =>
    {
        var list = args.ToList();
        var i = list.IndexOf("--output_file");
        if (i >= 0 && i + 1 < list.Count) File.WriteAllBytes(list[i + 1], FakeWav);
        return new ProcessRunResult(0, "", "", false);
    });

    public void Dispose()
    {
        try { if (File.Exists(_modelPath)) File.Delete(_modelPath); } catch { /* best effort */ }
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly Func<IReadOnlyList<string>, ProcessRunResult> _responder;
        public string? FileName;
        public IReadOnlyList<string>? Args;
        public string? Stdin;

        public FakeProcessRunner(Func<IReadOnlyList<string>, ProcessRunResult> responder) => _responder = responder;

        public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? stdin, TimeSpan timeout, CancellationToken ct)
        {
            FileName = fileName;
            Args = arguments;
            Stdin = stdin;
            return Task.FromResult(_responder(arguments));
        }
    }
}
