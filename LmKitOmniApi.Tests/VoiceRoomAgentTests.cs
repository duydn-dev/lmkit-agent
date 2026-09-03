using LmKitOmniApi.Infrastructure.AI.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pure unit tests for the voice room agent's STT → LLM → TTS turn orchestration, driven
/// entirely by fakes. No model, no audio, no LiveKit — this is the CI-verifiable core of the
/// otherwise live-only room agent. Also exercises the internal session loop wiring
/// (join → read → turn → publish → leave) with a fake media session.
/// </summary>
public sealed class VoiceRoomAgentTests
{
    [Fact]
    public async Task RunTurn_DrivesSttThenLlmThenTts_InOrder()
    {
        var callLog = new List<string>();
        var agent = new VoiceRoomAgent(
            new RecordingStt(callLog, "hello there"),
            new RecordingLlm(callLog, "hi, how can I help?"),
            new RecordingTts(callLog) { Output = new byte[] { 1, 2, 3 } },
            NullLogger<VoiceRoomAgent>.Instance);

        var result = await agent.RunTurnAsync(new VoiceTurnContext
        {
            InboundAudio = new byte[] { 9, 9 },
            Voice = "alto"
        });

        Assert.Equal(new[] { "stt", "llm", "tts" }, callLog);
        Assert.True(result.Handled);
        Assert.False(result.Skipped);
        Assert.Equal("hello there", result.UserText);
        Assert.Equal("hi, how can I help?", result.ReplyText);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.ReplyAudio);
    }

    [Fact]
    public async Task RunTurn_PassesRequestedVoiceToSynthesizer()
    {
        var callLog = new List<string>();
        var tts = new RecordingTts(callLog);
        var agent = new VoiceRoomAgent(
            new RecordingStt(callLog, "hi"),
            new RecordingLlm(callLog, "reply"),
            tts,
            NullLogger<VoiceRoomAgent>.Instance);

        await agent.RunTurnAsync(new VoiceTurnContext { InboundAudio = new byte[] { 1 }, Voice = "narrator" });

        Assert.Equal("narrator", tts.LastVoice);
    }

    [Fact]
    public async Task RunTurn_EmptyTranscript_SkipsLlmAndTts()
    {
        var callLog = new List<string>();
        var agent = new VoiceRoomAgent(
            new RecordingStt(callLog, "   "),      // silence → whitespace transcript
            new RecordingLlm(callLog, "must not run"),
            new RecordingTts(callLog),
            NullLogger<VoiceRoomAgent>.Instance);

        var result = await agent.RunTurnAsync(new VoiceTurnContext { InboundAudio = ReadOnlyMemory<byte>.Empty });

        Assert.Equal(new[] { "stt" }, callLog);     // LLM + TTS never called
        Assert.True(result.Skipped);
        Assert.False(result.Handled);
        Assert.Equal("no-speech-detected", result.SkipReason);
        Assert.Empty(result.ReplyAudio);
    }

    [Fact]
    public async Task RunTurn_EmptyLlmReply_SkipsTts()
    {
        var callLog = new List<string>();
        var agent = new VoiceRoomAgent(
            new RecordingStt(callLog, "hello"),
            new RecordingLlm(callLog, ""),          // model produced nothing
            new RecordingTts(callLog),
            NullLogger<VoiceRoomAgent>.Instance);

        var result = await agent.RunTurnAsync(new VoiceTurnContext { InboundAudio = new byte[] { 1 } });

        Assert.Equal(new[] { "stt", "llm" }, callLog); // TTS never called
        Assert.True(result.Skipped);
        Assert.Equal("empty-llm-response", result.SkipReason);
        Assert.Equal("hello", result.UserText);
    }

    [Fact]
    public async Task RunTurn_TtsUnavailable_ReturnsTextOnlyReply()
    {
        var callLog = new List<string>();
        var agent = new VoiceRoomAgent(
            new RecordingStt(callLog, "hello"),
            new RecordingLlm(callLog, "a spoken reply"),
            new RecordingTts(callLog) { IsAvailable = false },
            NullLogger<VoiceRoomAgent>.Instance);

        var result = await agent.RunTurnAsync(new VoiceTurnContext { InboundAudio = new byte[] { 1 } });

        Assert.Equal(new[] { "stt", "llm" }, callLog); // TTS synth skipped when unavailable
        Assert.True(result.Handled);
        Assert.Equal("a spoken reply", result.ReplyText);
        Assert.Empty(result.ReplyAudio);               // never fabricates audio
    }

    [Fact]
    public async Task RunSession_JoinsLoopsTurnsPublishesReply_AndLeaves()
    {
        var callLog = new List<string>();
        var agent = new VoiceRoomAgent(
            new RecordingStt(callLog, "hi"),
            new RecordingLlm(callLog, "reply"),
            new RecordingTts(callLog) { Output = new byte[] { 7, 7 } },
            NullLogger<VoiceRoomAgent>.Instance);
        var session = new FakeMediaSession(new byte[] { 1 }); // one utterance, then room closes

        await VoiceRoomAgentHostedService.RunSessionAsync(
            session, agent, new VoiceRoomOptions { Room = "r", Voice = "v" }, CancellationToken.None);

        Assert.True(session.Joined);
        Assert.True(session.Left);
        var published = Assert.Single(session.Published);
        Assert.Equal(new byte[] { 7, 7 }, published);
    }

    [Fact]
    public async Task RunSession_SilentUtterance_PublishesNothing()
    {
        var callLog = new List<string>();
        var agent = new VoiceRoomAgent(
            new RecordingStt(callLog, ""),          // silence → skipped turn
            new RecordingLlm(callLog, "unused"),
            new RecordingTts(callLog),
            NullLogger<VoiceRoomAgent>.Instance);
        var session = new FakeMediaSession(new byte[] { 1 });

        await VoiceRoomAgentHostedService.RunSessionAsync(
            session, agent, new VoiceRoomOptions(), CancellationToken.None);

        Assert.True(session.Joined);
        Assert.True(session.Left);
        Assert.Empty(session.Published);
    }

    private sealed class RecordingStt : IVoiceTurnStt
    {
        private readonly List<string> _log;
        private readonly string _transcript;
        public RecordingStt(List<string> log, string transcript) { _log = log; _transcript = transcript; }
        public Task<string> TranscribeAsync(ReadOnlyMemory<byte> audio, CancellationToken ct = default)
        {
            _log.Add("stt");
            return Task.FromResult(_transcript);
        }
    }

    private sealed class RecordingLlm : IVoiceTurnLlm
    {
        private readonly List<string> _log;
        private readonly string _reply;
        public RecordingLlm(List<string> log, string reply) { _log = log; _reply = reply; }
        public Task<string> RespondAsync(string userUtterance, CancellationToken ct = default)
        {
            _log.Add("llm");
            return Task.FromResult(_reply);
        }
    }

    private sealed class RecordingTts : ISpeechSynthesizer
    {
        private readonly List<string> _log;
        public RecordingTts(List<string> log) { _log = log; }
        public bool IsAvailable { get; set; } = true;
        public byte[] Output { get; set; } = new byte[] { 0 };
        public string? LastVoice { get; private set; }
        public Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken ct = default)
        {
            _log.Add("tts");
            LastVoice = voice;
            return Task.FromResult(Output);
        }
    }

    private sealed class FakeMediaSession : ILiveKitMediaSession
    {
        private readonly Queue<ReadOnlyMemory<byte>?> _utterances = new();
        public bool Joined { get; private set; }
        public bool Left { get; private set; }
        public List<byte[]> Published { get; } = new();

        public FakeMediaSession(params byte[][] utterances)
        {
            foreach (var utterance in utterances) _utterances.Enqueue(utterance);
            _utterances.Enqueue(null); // sentinel: room closed
        }

        public Task JoinAsync(VoiceRoomOptions options, CancellationToken ct = default)
        {
            Joined = true;
            return Task.CompletedTask;
        }

        public Task<ReadOnlyMemory<byte>?> ReadUtteranceAsync(CancellationToken ct = default) =>
            Task.FromResult(_utterances.Count > 0 ? _utterances.Dequeue() : null);

        public Task PublishAsync(ReadOnlyMemory<byte> wavAudio, CancellationToken ct = default)
        {
            Published.Add(wavAudio.ToArray());
            return Task.CompletedTask;
        }

        public Task LeaveAsync(CancellationToken ct = default)
        {
            Left = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
