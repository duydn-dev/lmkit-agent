using System.Text;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.AI.Filters;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// T15 — <see cref="StreamingGuardrailGate"/>, the mid-stream secret redactor that
/// had zero tests. It exists to stop a secret escaping token-by-token before the
/// end-of-stream guardrail pass can see it, and a secret split across two SSE
/// chunks is precisely the case it was written for.
///
/// <para>Everything here runs against the REAL detector
/// (<see cref="PromptGuardService"/>) and the REAL full pass
/// (<see cref="OutputGuardrailFilter"/>) — both are pure regex, no model — so the
/// tests pin the actual production contract rather than a mock of it:</para>
/// <list type="number">
/// <item>a secret never appears in the emitted prefix, at ANY point in the stream,
/// no matter where the chunk boundaries fall;</item>
/// <item>the emitted prefix is always a genuine prefix of the full pass's output —
/// the invariant <c>AgentOrchestrator</c> checks before releasing the tail, and
/// whose failure makes it suppress the tail and log a divergence;</item>
/// <item>streamed chunks + tail concatenate BYTE-IDENTICALLY to the non-streaming
/// guardrail result.</item>
/// </list>
/// </summary>
public sealed class StreamingGuardrailGateTests
{
    // Mirrors StreamingGuardrailGate.HoldbackChars (private const).
    private const int HoldbackChars = 512;

    // Clean carrier text: no credential keyword, no SSN/email shape, so it can never
    // itself trip a hold rule or a detector.
    private const string FillerSentence = "The quick brown fox jumps over the lazy dog. ";

    private const string CredentialSecret = "API_KEY: sk-live-51H8xQ2abcdefghij";
    private const string CredentialValue = "sk-live-51H8xQ2abcdefghij";
    private const string SsnSecret = "123-45-6789";
    private const string EmailSecret = "bob.smith@example.com";

    // ── The headline case: a secret split across chunk boundaries ─────────

    /// <summary>
    /// Every way a token stream can slice a credential — before the keyword, inside
    /// the keyword, on the separator, inside the value, and one character at a time.
    /// </summary>
    public static TheoryData<int> CredentialSplitPoints() =>
        new(Enumerable.Range(0, CredentialSecret.Length + 1));

    [Theory]
    [MemberData(nameof(CredentialSplitPoints))]
    public async Task CredentialSplitAcrossTwoChunks_NeverEscapes_AtAnySplitPoint(int splitAt)
    {
        var (prefix, suffix) = ("Đây là cấu hình bạn yêu cầu: ", ". " + Filler(40));

        var outcome = await RunAsync(
            [prefix, CredentialSecret[..splitAt], CredentialSecret[splitAt..], suffix],
            secretMarkers: [CredentialValue, CredentialValue[..4]]);

        AssertRedactedAndConsistent(outcome, CredentialValue);
        // The keyword survives redaction; only its value is scrubbed.
        Assert.Contains("API_KEY: [REDACTED]", outcome.Final);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(512)]
    public async Task CredentialStreamedAtEveryChunkSize_NeverEscapes(int chunkSize)
    {
        var document = "Cấu hình: " + CredentialSecret + ". " + Filler(40);

        var outcome = await RunAsync(
            SplitEvery(document, chunkSize),
            secretMarkers: [CredentialValue, CredentialValue[..4]]);

        AssertRedactedAndConsistent(outcome, CredentialValue);
    }

    [Fact]
    public async Task CredentialAtEveryOffsetInTheStream_NeverEscapes()
    {
        // Sweeps the secret across the emit zone, the holdback window and the boundary
        // between them — the one place where "already released" and "still held" meet.
        for (var offset = 0; offset <= 1400; offset += 37)
        {
            var document = Filler(40)[..offset] + " " + CredentialSecret + ". " + Filler(40);

            var outcome = await RunAsync(
                SplitEvery(document, 7),
                secretMarkers: [CredentialValue, CredentialValue[..4]]);

            AssertRedactedAndConsistent(outcome, CredentialValue);
        }
    }

    [Theory]
    [InlineData(3)]  // "123" | "-45-6789"
    [InlineData(6)]  // "123-45" | "-6789"
    [InlineData(7)]  // "123-45-" | "6789"
    [InlineData(10)] // "123-45-678" | "9"  — the completing digit arrives last
    public async Task SsnSplitAcrossTwoChunks_NeverEscapes(int splitAt)
    {
        var outcome = await RunAsync(
            ["Số an sinh xã hội: ", SsnSecret[..splitAt], SsnSecret[splitAt..], ". " + Filler(40)],
            secretMarkers: [SsnSecret, "123-45"]);

        AssertRedactedAndConsistent(outcome, SsnSecret);
        Assert.Contains("[SSN REDACTED]", outcome.Final);
    }

    [Theory]
    [InlineData(4)]  // "bob." | "smith@example.com"
    [InlineData(9)]  // "bob.smith" | "@example.com"
    [InlineData(10)] // "bob.smith@" | "example.com"
    [InlineData(18)] // "bob.smith@example." | "com"  — the TLD arrives last
    public async Task EmailSplitAcrossTwoChunks_NeverEscapes(int splitAt)
    {
        var outcome = await RunAsync(
            ["Liên hệ: ", EmailSecret[..splitAt], EmailSecret[splitAt..], ". " + Filler(40)],
            secretMarkers: [EmailSecret, "bob.smith@"]);

        AssertRedactedAndConsistent(outcome, EmailSecret);
        Assert.Contains("[EMAIL REDACTED]", outcome.Final);
    }

    [Fact]
    public async Task CredentialAndPiiInTheSameStream_BothClassesRedacted_AndNeitherEscapes()
    {
        var document = $"Thông tin: {CredentialSecret}, {EmailSecret}, {SsnSecret}. " + Filler(40);

        var outcome = await RunAsync(
            SplitEvery(document, 5),
            secretMarkers: [CredentialValue, EmailSecret, SsnSecret]);

        AssertRedactedAndConsistent(outcome, CredentialValue, EmailSecret, SsnSecret);
        Assert.Contains("[EMAIL REDACTED]", outcome.Final);
        Assert.Contains("[SSN REDACTED]", outcome.Final);
        Assert.Contains("[REDACTED]", outcome.Final);
    }

    [Fact]
    public async Task SecretArrivingAsTheVeryLastTokens_IsReleasedOnlyRedactedByTheTail()
    {
        // The secret lands entirely inside the holdback window and the stream then
        // ends: the gate can never emit it, so the redacted form reaches the client
        // only through the orchestrator's end-of-stream tail.
        var document = Filler(60) + " " + CredentialSecret;

        var outcome = await RunAsync(
            SplitEvery(document, 11),
            secretMarkers: [CredentialValue, CredentialValue[..4]]);

        AssertRedactedAndConsistent(outcome, CredentialValue);
        Assert.DoesNotContain("API_KEY", outcome.Emitted);
        Assert.Contains("API_KEY: [REDACTED]", outcome.Tail);
    }

    // ── Byte-identity with the non-streaming pass on clean input ──────────

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(97)]
    public async Task CleanAnswer_StreamsProgressively_AndConcatenatesByteIdenticalToTheFullPass(int chunkSize)
    {
        var document = Filler(60);

        var outcome = await RunAsync(SplitEvery(document, chunkSize), secretMarkers: []);

        // Nothing was detected, so the full pass is the identity — and the streamed
        // chunks plus the tail reproduce it exactly, character for character.
        Assert.Equal(document, outcome.Final);
        Assert.Equal(document, outcome.Emitted + outcome.Tail);
        Assert.StartsWith(outcome.Emitted, outcome.Final, StringComparison.Ordinal);
        // Real streaming happened: most of the answer arrived before the tail.
        Assert.NotEmpty(outcome.Emitted);
        Assert.Equal(document.Length - outcome.Emitted.Length, outcome.Tail.Length);
    }

    [Fact]
    public async Task ReturnedChunks_ConcatenateExactlyToEmittedText_AndOnlyEverGrow()
    {
        var joined = new StringBuilder();
        var lastLength = 0;

        var outcome = await RunAsync(
            SplitEvery(Filler(60), 9),
            secretMarkers: [],
            afterEach: (chunk, emitted) =>
            {
                joined.Append(chunk);
                Assert.True(emitted.Length >= lastLength, "EmittedText shrank mid-stream.");
                lastLength = emitted.Length;
            });

        Assert.Equal(outcome.Emitted, joined.ToString());
    }

    [Fact]
    public async Task OutputOverTheTruncationCap_StillConcatenatesToTheFullPassResult()
    {
        // Past OutputGuardrailFilter.MaxOutputLength the full pass truncates and appends
        // a notice; the gate caps emission at the same length and the tail carries the
        // notice, so the two paths still agree byte for byte.
        var document = Filler(500);
        Assert.True(document.Length > OutputGuardrailFilter.MaxOutputLength);

        var outcome = await RunAsync(SplitEvery(document, 64), secretMarkers: []);

        Assert.EndsWith(OutputGuardrailFilter.TruncationNotice, outcome.Final);
        Assert.Equal(OutputGuardrailFilter.MaxOutputLength, outcome.Emitted.Length);
        Assert.Equal(outcome.Final, outcome.Emitted + outcome.Tail);
    }

    // ── The holdback's cost: short answers never stream at all ────────────

    /// <summary>
    /// Documented finding (see AGENTRUN-FIX-INTEGRATION.md). The emission cap is
    /// <c>view.Length - HoldbackChars</c>, so NOTHING is released until the answer
    /// passes 512 characters — despite the call site advertising "TRUE token
    /// streaming". Typical chat answers are shorter than that, so they arrive in a
    /// single burst at the end of the stream.
    ///
    /// <para>NOT fixed: the holdback is what makes the redaction correct. Shrinking it
    /// re-opens the exact leak these tests pin — the email pattern's local part is
    /// unbounded, so a match can begin arbitrarily far behind the live edge and a
    /// shorter window would let its head stream out before the '@' proves it was an
    /// address. This test pins the behaviour so the trade-off stays visible and any
    /// future change to it is deliberate.</para>
    /// </summary>
    [Theory]
    [InlineData(64)]
    [InlineData(200)]
    [InlineData(511)]
    [InlineData(512)]
    public async Task AnswerShorterThanTheHoldbackWindow_StreamsNothingAtAll(int length)
    {
        var document = Filler(60)[..length];

        var outcome = await RunAsync(SplitEvery(document, 8), secretMarkers: []);

        Assert.Empty(outcome.Emitted);
        // The whole answer is released in one burst by the end-of-stream tail.
        Assert.Equal(document, outcome.Tail);
    }

    [Fact]
    public async Task AnswerJustPastTheHoldbackWindow_StartsStreaming()
    {
        // 513 chars is the first length that can release anything, and only the single
        // character beyond the window.
        var outcome = await RunAsync([Filler(60)[..513]], secretMarkers: []);

        Assert.Equal(1, outcome.Emitted.Length);
        Assert.Equal(513 - HoldbackChars, outcome.Emitted.Length);
    }

    [Fact]
    public async Task EmitStride_DefersReleaseUntilEnoughNewTextArrives()
    {
        // A final partial chunk smaller than the 32-char stride is not even evaluated,
        // so an answer can exceed the holdback and STILL stream nothing.
        var document = Filler(60)[..520];

        var outcome = await RunAsync(SplitEvery(document, 32), secretMarkers: []);

        Assert.Empty(outcome.Emitted);
        Assert.Equal(document, outcome.Tail);
    }

    // ── harness ───────────────────────────────────────────────────────────

    private sealed record StreamOutcome(string Raw, string Emitted, string Final)
    {
        /// <summary>
        /// The remainder <c>AgentOrchestrator</c> releases after the stream ends. Reading
        /// it asserts the prefix invariant, because the orchestrator's else-branch
        /// (suppress the tail, log a divergence) is a bug report, not a success path.
        /// </summary>
        public string Tail
        {
            get
            {
                Assert.True(
                    Final.StartsWith(Emitted, StringComparison.Ordinal),
                    "Streaming guardrail divergence: the emitted prefix is not a prefix of the full-pass result.");
                return Final[Emitted.Length..];
            }
        }
    }

    /// <summary>
    /// Streams <paramref name="chunks"/> through a gate exactly as
    /// <c>AgentOrchestrator</c> does, asserting after EVERY chunk that no marker in
    /// <paramref name="secretMarkers"/> has escaped into the emitted prefix, then runs
    /// the real non-streaming guardrail over the same raw text.
    /// </summary>
    private static async Task<StreamOutcome> RunAsync(
        IEnumerable<string> chunks,
        string[] secretMarkers,
        Action<string, string>? afterEach = null)
    {
        var guard = new PromptGuardService(NullLogger<PromptGuardService>.Instance);
        var gate = new StreamingGuardrailGate(guard);

        foreach (var chunk in chunks)
        {
            var emittedChunk = await gate.AppendAndTryEmitAsync(chunk, CancellationToken.None);
            var emittedSoFar = gate.EmittedText;

            foreach (var marker in secretMarkers)
            {
                Assert.False(
                    emittedSoFar.Contains(marker, StringComparison.Ordinal),
                    $"Secret marker '{marker}' escaped mid-stream after {emittedSoFar.Length} emitted chars.");
            }

            afterEach?.Invoke(emittedChunk, emittedSoFar);
        }

        var raw = gate.RawText;
        var filter = new OutputGuardrailFilter(guard, NullLogger<OutputGuardrailFilter>.Instance);
        var full = await filter.OnOutputAsync(new AgentFilterContext { Output = raw });

        return new StreamOutcome(raw, gate.EmittedText, full.ProcessedContent);
    }

    /// <summary>
    /// The three properties every secret-bearing stream must satisfy: the secret is
    /// gone from BOTH the streamed prefix and the authoritative full pass, the prefix
    /// invariant holds, and the two paths concatenate to the same bytes.
    /// </summary>
    private static void AssertRedactedAndConsistent(StreamOutcome outcome, params string[] secrets)
    {
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, outcome.Emitted, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, outcome.Final, StringComparison.Ordinal);
            // The raw model output really did contain it — otherwise the test proves nothing.
            Assert.Contains(secret, outcome.Raw, StringComparison.Ordinal);
        }

        Assert.Equal(outcome.Final, outcome.Emitted + outcome.Tail);
    }

    private static string Filler(int repeats)
        => string.Concat(Enumerable.Repeat(FillerSentence, repeats));

    private static IEnumerable<string> SplitEvery(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }
}
