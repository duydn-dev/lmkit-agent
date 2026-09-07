using LmKitOmniApi.Infrastructure.AI.Voice;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The voice room CONTRACT: one room-naming function and one credential source shared by the
/// browser token endpoint (<c>SpeechController.GetLiveKitToken</c>) and the server-side agent
/// (<c>VoiceRoomAgentHostedService</c>).
///
/// These pin the two defects that made the feature unusable: the endpoint minted
/// <c>{tenant}-{room}</c> while the agent joined the bare <c>Voice:Room</c> label (two
/// different rooms — the agent could never hear anyone), and rooms were scoped per TENANT, so
/// two colleagues in one tenant shared a room and could hear each other.
/// </summary>
public sealed class VoiceRoomNamingTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid UserB = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void ScopedRoom_IncludesTenantAndUser()
    {
        Assert.True(VoiceRoomNaming.TryScopedRoom(Tenant, UserA, "omni-room", out var room, out var error));
        Assert.Null(error);
        Assert.Equal($"{Tenant:N}-{UserA:N}-omni-room", room);
    }

    [Fact]
    public void TwoUsersInOneTenant_NeverShareARoom()
    {
        Assert.True(VoiceRoomNaming.TryScopedRoom(Tenant, UserA, "omni-room", out var roomA, out _));
        Assert.True(VoiceRoomNaming.TryScopedRoom(Tenant, UserB, "omni-room", out var roomB, out _));
        Assert.NotEqual(roomA, roomB);
    }

    [Fact]
    public void AgentRoom_MatchesTheRoomTheTokenEndpointWouldMint()
    {
        // THE regression: the agent must compute the same room name as the caller's token.
        var options = new VoiceOptions
        {
            Room = "omni-room",
            AgentTenantId = Tenant.ToString(),
            AgentUserId = UserA.ToString(),
        };

        Assert.True(options.TryResolveAgentRoom(out var agentRoom, out var error));
        Assert.Null(error);
        Assert.True(VoiceRoomNaming.TryScopedRoom(Tenant, UserA, options.Room, out var callerRoom, out _));
        Assert.Equal(callerRoom, agentRoom);
        Assert.NotEqual(options.Room, agentRoom); // never the bare label it used to join
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void AgentRoom_WithoutAValidIdentity_IsRefusedWithAReason(string tenantId)
    {
        var options = new VoiceOptions { AgentTenantId = tenantId, AgentUserId = UserA.ToString() };

        Assert.False(options.TryResolveAgentRoom(out var room, out var error));
        Assert.Equal(string.Empty, room);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Label_IsSanitizedAndDefaulted()
    {
        Assert.True(VoiceRoomNaming.TrySanitizeLabel(null, out var fallback, out _));
        Assert.Equal(VoiceRoomNaming.DefaultLabel, fallback);

        Assert.True(VoiceRoomNaming.TrySanitizeLabel("my room/../etc", out var safe, out _));
        Assert.Equal("my-room-etc", safe);

        // At the input cap the label is accepted but truncated to the bounded room-name length.
        Assert.True(VoiceRoomNaming.TrySanitizeLabel(
            new string('a', VoiceRoomNaming.MaxLabelInputLength), out var capped, out _));
        Assert.Equal(VoiceRoomNaming.MaxSanitizedLabelLength, capped.Length);

        Assert.False(VoiceRoomNaming.TrySanitizeLabel(
            new string('a', VoiceRoomNaming.MaxLabelInputLength + 1), out _, out var tooLong));
        Assert.False(string.IsNullOrWhiteSpace(tooLong));
        Assert.False(VoiceRoomNaming.TrySanitizeLabel("///", out _, out var unusable));
        Assert.False(string.IsNullOrWhiteSpace(unusable));
    }

    [Fact]
    public void ScopedRoom_RequiresANonEmptyIdentity()
    {
        Assert.False(VoiceRoomNaming.TryScopedRoom(Guid.Empty, UserA, "r", out _, out var tenantError));
        Assert.False(string.IsNullOrWhiteSpace(tenantError));
        Assert.False(VoiceRoomNaming.TryScopedRoom(Tenant, Guid.Empty, "r", out _, out var userError));
        Assert.False(string.IsNullOrWhiteSpace(userError));
    }
}

/// <summary>
/// One credential source. The token endpoint used to read <c>LiveKit:ApiKey/ApiSecret</c>
/// while the hosted agent read <c>Voice:LiveKitApiKey/LiveKitApiSecret</c>, so configuring one
/// left the other half of the feature dead with no diagnostic.
/// </summary>
public sealed class VoiceLiveKitCredentialsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
            .Build();

    [Fact]
    public void SharedLiveKitBlock_ConfiguresTheAgentToo()
    {
        var config = Config(("LiveKit:Url", "ws://livekit:7880"), ("LiveKit:ApiKey", "k"), ("LiveKit:ApiSecret", "s"));

        var resolved = VoiceLiveKitCredentials.Resolve(new VoiceOptions(), config);

        Assert.True(resolved.IsConfigured);
        Assert.True(resolved.CanJoin);
        Assert.Equal("k", resolved.ApiKey);
        Assert.Equal("s", resolved.ApiSecret);
        Assert.Equal("ws://livekit:7880", resolved.Url);
    }

    [Fact]
    public void VoiceKeys_ConfigureTheTokenEndpointToo_AndWinOnConflict()
    {
        var config = Config(("LiveKit:ApiKey", "shared"), ("LiveKit:ApiSecret", "shared-secret"));
        var options = new VoiceOptions { LiveKitApiKey = "voice", LiveKitApiSecret = "voice-secret" };

        var resolved = VoiceLiveKitCredentials.Resolve(options, config);

        Assert.Equal("voice", resolved.ApiKey);
        Assert.Equal("voice-secret", resolved.ApiSecret);
    }

    [Fact]
    public void PartialOverride_FallsBackPerField()
    {
        var config = Config(("LiveKit:Url", "ws://shared:7880"), ("LiveKit:ApiKey", "k"), ("LiveKit:ApiSecret", "s"));
        var options = new VoiceOptions { LiveKitUrl = "wss://voice.example/" };

        var resolved = VoiceLiveKitCredentials.Resolve(options, config);

        Assert.Equal("wss://voice.example/", resolved.Url);
        Assert.Equal("k", resolved.ApiKey);
        Assert.Equal("s", resolved.ApiSecret);
    }

    [Fact]
    public void NothingConfigured_IsReportedAsUnconfigured_NotFabricated()
    {
        var resolved = VoiceLiveKitCredentials.Resolve(new VoiceOptions(), Config());

        Assert.False(resolved.IsConfigured);
        Assert.False(resolved.CanJoin);
        Assert.Equal(string.Empty, resolved.ApiKey);
    }

    [Fact]
    public void MissingUrl_CannotJoin_EvenWithCredentials()
    {
        var resolved = VoiceLiveKitCredentials.Resolve(
            new VoiceOptions { LiveKitApiKey = "k", LiveKitApiSecret = "s" }, Config());

        Assert.True(resolved.IsConfigured);
        Assert.False(resolved.CanJoin);
    }
}

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
