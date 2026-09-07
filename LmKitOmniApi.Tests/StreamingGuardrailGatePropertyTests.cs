using System.Text;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.AI.Filters;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Randomized + adversarial verification of the ONE property
/// <see cref="StreamingGuardrailGate"/> exists to provide, and the one
/// <c>AgentOrchestrator</c> checks before it releases the end-of-stream tail:
///
/// <list type="number">
/// <item><c>finalContent.StartsWith(emitted)</c> — what was streamed is a genuine
/// prefix of the guardrail-processed answer;</item>
/// <item><c>emitted + remainder == finalContent</c> — byte-identical, nothing
/// duplicated, nothing lost;</item>
/// <item>no secret ever reaches the emitted prefix.</item>
/// </list>
///
/// <para>Every case runs against the REAL detector (<see cref="PromptGuardService"/>)
/// and the REAL full pass (<see cref="OutputGuardrailFilter"/>) — both pure regex, no
/// model — so this pins the production contract, not a mock of it. Seeds are fixed and
/// printed on failure, so any counterexample is reproducible by re-running the single
/// theory case that reports it.</para>
/// </summary>
public sealed class StreamingGuardrailGatePropertyTests
{
    private const int SeedsPerBatch = 25;
    private const int BatchCount = 12;   // 300 randomized documents in total

    /// <summary>Seed batches, so a failure names a small range rather than one big Fact.</summary>
    public static TheoryData<int> SeedBatches()
        => new(Enumerable.Range(0, BatchCount));

    [Theory]
    [MemberData(nameof(SeedBatches))]
    public async Task RandomDocuments_StreamedInRandomChunks_AlwaysSatisfyThePrefixContract(int batch)
    {
        var scrubbed = 0;

        for (var seed = batch * SeedsPerBatch; seed < (batch + 1) * SeedsPerBatch; seed++)
        {
            var rng = new Random(seed);
            var (document, secrets) = BuildDocument(rng);
            var chunks = RandomChunks(document, rng).ToArray();

            scrubbed += (await AssertContractAsync(seed, document, chunks, secrets)).SecretsScrubbed;
        }

        // Keeps the batch from passing vacuously: these documents really do carry
        // secrets that the guardrail removes, so the mid-stream checks have teeth.
        Assert.True(scrubbed >= SeedsPerBatch, $"Only {scrubbed} secrets were actually redacted across the batch.");
    }

    // ── adversarial fixtures (deterministic, one per known failure mode) ──────

    /// <summary>
    /// An address split at EVERY offset, with the split landing on a real chunk
    /// boundary. Before the '@' arrives the head of the local part looks like plain
    /// prose; the hold rule is what stops it streaming out.
    /// </summary>
    public static TheoryData<int> EmailSplitOffsets()
        => new(Enumerable.Range(0, "nguyen.van.a+tag@mail.example.co.uk".Length + 1));

    [Theory]
    [MemberData(nameof(EmailSplitOffsets))]
    public async Task EmailSplitExactlyOnAChunkBoundary_NeverEscapes(int splitAt)
    {
        const string email = "nguyen.van.a+tag@mail.example.co.uk";
        var lead = "Bạn có thể liên hệ qua địa chỉ ";
        var chunks = new[] { lead, email[..splitAt], email[splitAt..], " nhé. " + Prose(20) };

        await AssertContractAsync(splitAt, string.Concat(chunks), chunks, [email, email[..Math.Max(3, splitAt)]], requireAllScrubbed: true);
    }

    /// <summary>
    /// The document ENDS exactly on a completed match — the case where the gate must
    /// not treat "it matches right now" as "it is settled", because one more character
    /// could dissolve it (SSN) or extend it (email, credential value).
    /// </summary>
    [Theory]
    [InlineData("123-45-6789")]
    [InlineData("123 45 6789")]
    [InlineData("bob.smith@example.com")]
    [InlineData("a@b.co")]
    [InlineData("API_KEY: sk-live-abcdef")]
    [InlineData("Bearer eyJhbGciOi")]
    [InlineData("password=hunter2")]
    public async Task MatchEndingExactlyAtTheLiveEdge_NeverEscapes(string secret)
    {
        var document = Prose(30) + " " + secret;

        await AssertContractAsync(0, document, RandomChunks(document, new Random(7)).ToArray(), [secret], requireAllScrubbed: true);
    }

    /// <summary>
    /// A match that is complete now and grows LATER: "a@b.co" is a valid address, and
    /// "a@b.co" + ".uk" is a longer one covering the same start. A gate that redacted
    /// the short form and released it would diverge from the full pass.
    /// </summary>
    [Theory]
    [InlineData(".uk")]
    [InlineData(".com.vn")]
    [InlineData("m")]
    public async Task MatchThatGrowsAfterItAlreadyMatched_NeverDiverges(string growth)
    {
        var head = Prose(30) + " lien he a@b.co";
        var chunks = new[] { head, growth, " " + Prose(20) };

        await AssertContractAsync(0, string.Concat(chunks), chunks, ["a@b.co"], requireAllScrubbed: true);
    }

    /// <summary>
    /// A credential keyword followed by a long whitespace run before its value: the
    /// pattern's "\s*[:=]?\s*" is unbounded, so this is the construct the hold rule
    /// has to walk backwards over. Beyond the 512-char clamp it becomes the documented
    /// residual, so this stays inside it.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(80)]
    [InlineData(400)]
    public async Task CredentialKeywordFollowedByWhitespaceRun_NeverEscapes(int gap)
    {
        var document = Prose(30) + " API_KEY:" + new string(' ', gap) + "sk-live-zzz9. " + Prose(10);

        await AssertContractAsync(gap, document, RandomChunks(document, new Random(gap)).ToArray(), ["sk-live-zzz9"], requireAllScrubbed: true);
    }

    /// <summary>
    /// Near-misses: shapes that look like a secret until one more character proves they
    /// are not. Neither the streamed prefix nor the full pass may redact these, and the
    /// two must still agree.
    /// </summary>
    [Theory]
    [InlineData("123-45-678")]
    [InlineData("1234-56-7890")]
    [InlineData("bob@example")]
    [InlineData("bob@example.c")]
    [InlineData("@example.com")]
    [InlineData("TOKENISED")]
    public async Task NearMissShapes_AreNotRedactedAndStillAgree(string shape)
    {
        var document = Prose(20) + " " + shape + " " + Prose(20);

        var outcome = await AssertContractAsync(0, document, RandomChunks(document, new Random(3)).ToArray(), []);

        Assert.Equal(document, outcome.Final);
    }

    /// <summary>
    /// Every secret class in one stream, chunked one character at a time — the slowest
    /// and most boundary-hostile way a model can deliver them.
    /// </summary>
    [Fact]
    public async Task AllSecretClassesOneCharacterAtATime_NeverEscape()
    {
        var document =
            "Thông tin: API_KEY: sk-live-51H8xQ2, email bob.smith@example.com, SSN 123-45-6789, "
            + "Bearer eyJhbGciOiJIUzI1NiJ9. " + Prose(20);

        var chunks = document.Select(c => c.ToString()).ToArray();

        await AssertContractAsync(
            0, document, chunks,
            ["sk-live-51H8xQ2", "bob.smith@example.com", "123-45-6789", "eyJhbGciOiJIUzI1NiJ9"],
            requireAllScrubbed: true);
    }

    /// <summary>
    /// Past <see cref="OutputGuardrailFilter.MaxOutputLength"/> the full pass truncates
    /// and appends a notice, and it does so on the POST-redaction text — so the gate's
    /// cap has to be measured in the same coordinates.
    /// </summary>
    [Fact]
    public async Task OverTheTruncationCapWithRedactions_StillAgrees()
    {
        var sb = new StringBuilder();
        var rng = new Random(99);
        while (sb.Length < OutputGuardrailFilter.MaxOutputLength + 3000)
        {
            sb.Append(Prose(6)).Append(' ');
            if (rng.Next(4) == 0) sb.Append("API_KEY: sk-").Append(rng.Next(100000)).Append(". ");
            if (rng.Next(6) == 0) sb.Append("user").Append(rng.Next(1000)).Append("@example.com. ");
        }
        var document = sb.ToString();

        var outcome = await AssertContractAsync(99, document, RandomChunks(document, rng).ToArray(), []);

        Assert.EndsWith(OutputGuardrailFilter.TruncationNotice, outcome.Final);
    }

    /// <summary>
    /// The two constructs that defeated the old flat-512 holdback, at lengths that
    /// straddle it. Both used to diverge (silently truncating the user's answer);
    /// with an exact hold neither does.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(520)]
    [InlineData(700)]
    [InlineData(1200)]
    public async Task EmailLocalPartLongerThanTheOldHoldback_NeverDiverges(int localLength)
    {
        var local = new string('a', localLength);
        var email = local + "@example.com";
        var document = "prefix text here " + email + " and more text after it.";

        await AssertContractAsync(
            localLength, document, RandomChunks(document, new Random(localLength)).ToArray(),
            [email], requireAllScrubbed: true);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(511)]
    [InlineData(700)]
    [InlineData(1500)]
    public async Task CredentialKeywordFollowedByAWhitespaceRunLongerThanTheOldHoldback_NeverDiverges(int gap)
    {
        // The leading credential latches the class, which is the half of the old
        // residual that the unlatched keyword cap did not already cover.
        var document = "API_KEY: firstsecret1. now some prose. TOKEN:"
                       + new string(' ', gap) + "SECONDSECRET2 and more text.";

        await AssertContractAsync(
            gap, document, RandomChunks(document, new Random(gap)).ToArray(),
            ["firstsecret1", "SECONDSECRET2"], requireAllScrubbed: true);
    }

    /// <summary>
    /// Finding surfaced by the randomized corpus, OUTSIDE this gate's scope and NOT
    /// fixed here. <see cref="OutputGuardrailFilter.CredentialRedactionPattern"/> makes
    /// the ':'/'=' separator optional, but <c>PromptGuardService</c>'s detection pattern
    /// makes it mandatory — and the filter only redacts when the detector fires. So a
    /// credential written with a plain space ("SECRET_KEY hunter2") is never detected
    /// and therefore never redacted, unless some OTHER credential in the same answer
    /// happens to trip the detector, at which point the broader redaction pattern
    /// scrubs it too.
    ///
    /// <para>This test pins the current behaviour so the gate's own tests cannot
    /// silently assume a scrub that the product does not perform. The gate is correct
    /// either way: it holds nothing back that the full pass would have kept.</para>
    /// </summary>
    [Fact]
    public async Task CredentialsWithoutASeparatorAreNeverDetected()
    {
        var guard = new PromptGuardService(NullLogger<PromptGuardService>.Instance);
        var filter = new OutputGuardrailFilter(guard, NullLogger<OutputGuardrailFilter>.Instance);

        var spaceOnly = await filter.OnOutputAsync(new AgentFilterContext { Output = "SECRET_KEY hunter2xyz" });
        Assert.Contains("hunter2xyz", spaceOnly.ProcessedContent);

        // One colon anywhere in the answer flips the whole answer into redaction.
        var withColon = await filter.OnOutputAsync(
            new AgentFilterContext { Output = "TOKEN: abc123. SECRET_KEY hunter2xyz" });
        Assert.DoesNotContain("hunter2xyz", withColon.ProcessedContent);
    }

    // ── the contract check ───────────────────────────────────────────────────

    private sealed record Outcome(string Raw, string Emitted, string Final, int SecretsScrubbed);

    /// <summary>
    /// Streams <paramref name="chunks"/> through a gate exactly as
    /// <c>AgentOrchestrator</c> does and asserts the three contract properties.
    ///
    /// <para><paramref name="secrets"/> are the values planted in the document. Only
    /// the ones the full pass actually removes are policed mid-stream — a value the
    /// product never redacts (see <see cref="CredentialsWithoutASeparatorAreNeverDetected"/>)
    /// is not something the gate is allowed to withhold. Pass
    /// <paramref name="requireAllScrubbed"/> for fixtures built so that every planted
    /// value MUST be redacted, which keeps those cases from silently going vacuous.</para>
    /// </summary>
    private static async Task<Outcome> AssertContractAsync(
        int seed, string document, string[] chunks, string[] secrets, bool requireAllScrubbed = false)
    {
        var guard = new PromptGuardService(NullLogger<PromptGuardService>.Instance);

        // The authoritative answer, computed up front: it decides which planted values
        // the gate is obliged to hold back.
        var filter = new OutputGuardrailFilter(guard, NullLogger<OutputGuardrailFilter>.Instance);
        var full = await filter.OnOutputAsync(new AgentFilterContext { Output = document });
        var final = full.ProcessedContent ?? string.Empty;

        var scrubbed = secrets.Where(s => !final.Contains(s, StringComparison.Ordinal)).ToArray();
        if (requireAllScrubbed)
        {
            foreach (var secret in secrets)
            {
                Assert.False(
                    final.Contains(secret, StringComparison.Ordinal),
                    Where(seed, document, $"full pass left secret '{Trim(secret)}' in the answer."));
            }
        }

        var gate = new StreamingGuardrailGate(guard);
        var joined = new StringBuilder();
        var lastEmittedLength = 0;

        foreach (var chunk in chunks)
        {
            var emittedChunk = await gate.AppendAndTryEmitAsync(chunk, CancellationToken.None);
            joined.Append(emittedChunk);

            var emitted = gate.EmittedText;
            Assert.True(emitted.Length >= lastEmittedLength, Where(seed, document, "EmittedText shrank mid-stream."));
            lastEmittedLength = emitted.Length;

            // The returned chunks are exactly what EmittedText claims was released.
            Assert.Equal(emitted, joined.ToString());

            foreach (var secret in scrubbed)
            {
                Assert.False(
                    emitted.Contains(secret, StringComparison.Ordinal),
                    Where(seed, document, $"secret '{Trim(secret)}' escaped after {emitted.Length} emitted chars."));
            }
        }

        Assert.Equal(document, gate.RawText);
        var emittedText = gate.EmittedText;

        // (1) the prefix invariant AgentOrchestrator checks before releasing the tail.
        //     Its else-branch suppresses the tail and logs an error, so a failure here
        //     is a truncated answer in production, not a cosmetic problem.
        Assert.True(
            final.StartsWith(emittedText, StringComparison.Ordinal),
            Where(seed, document,
                $"streaming guardrail divergence: {emittedText.Length} emitted chars are not a prefix of the "
                + $"{final.Length}-char full-pass result. First difference at "
                + $"{FirstDifference(final, emittedText)}."));

        // (2) streamed chunks + the orchestrator's tail reproduce the full pass exactly.
        var remainder = final[emittedText.Length..];
        Assert.Equal(final, joined + remainder);

        return new Outcome(document, emittedText, final, scrubbed.Length);
    }

    private static int FirstDifference(string final, string emitted)
    {
        var n = Math.Min(final.Length, emitted.Length);
        for (var i = 0; i < n; i++)
            if (final[i] != emitted[i]) return i;
        return n;
    }

    private static string Where(int seed, string document, string message)
        => $"seed={seed}: {message}\n  document({document.Length} chars)={Trim(document)}";

    private static string Trim(string s)
        => (s.Length <= 400 ? s : s[..200] + " … " + s[^200..]).Replace("\n", "\\n");

    // ── generators ───────────────────────────────────────────────────────────

    private static readonly string[] Words =
    [
        "hệ", "thống", "cấu", "hình", "của", "bạn", "đã", "sẵn", "sàng", "the", "quick", "brown",
        "fox", "jumps", "over", "lazy", "dog", "config", "value", "note", "step", "one", "two",
    ];

    private static string Prose(int words)
    {
        var rng = new Random(words * 7919);
        var sb = new StringBuilder();
        for (var i = 0; i < words; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(Words[rng.Next(Words.Length)]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds one document out of weighted fragments and reports the literal secrets it
    /// planted, so the caller can assert none of them ever reached the client early.
    /// </summary>
    private static (string Document, string[] Secrets) BuildDocument(Random rng)
    {
        var sb = new StringBuilder();
        var secrets = new List<string>();
        var fragments = rng.Next(3, 30);

        for (var i = 0; i < fragments; i++)
        {
            switch (rng.Next(14))
            {
                case 0 or 1 or 2 or 3 or 4:      // plain prose — the common case
                    sb.Append(RandomWords(rng, rng.Next(1, 12))).Append(rng.Next(3) == 0 ? ". " : " ");
                    break;

                case 5:                           // an email address
                {
                    var email = $"{RandomToken(rng, 3, 10)}.{RandomToken(rng, 2, 8)}@{RandomToken(rng, 3, 9)}.example.com";
                    secrets.Add(email);
                    sb.Append(email).Append(rng.Next(2) == 0 ? ", " : " ");
                    break;
                }

                case 6:                           // an SSN, every separator flavour
                {
                    var sep = new[] { "-", ".", " ", "" }[rng.Next(4)];
                    var ssn = $"{rng.Next(100, 1000)}{sep}{rng.Next(10, 100)}{sep}{rng.Next(1000, 10000)}";
                    secrets.Add(ssn);
                    sb.Append(ssn).Append(' ');
                    break;
                }

                case 7:                           // a credential, every keyword/separator flavour
                {
                    var keyword = new[] { "API_KEY", "API KEY", "APIKEY", "SECRET_KEY", "SECRET KEY", "PASSWORD", "TOKEN", "Bearer", "api-key" }[rng.Next(9)];
                    var sep = new[] { ": ", "=", " = ", " ", ":", "  :  " }[rng.Next(6)];
                    var value = RandomToken(rng, 6, 28);
                    secrets.Add(value);
                    sb.Append(keyword).Append(sep).Append(value).Append(". ");
                    break;
                }

                case 8:                           // keyword then a WHITESPACE RUN then the value
                {
                    var value = RandomToken(rng, 6, 20);
                    secrets.Add(value);
                    sb.Append("TOKEN:").Append(new string(' ', rng.Next(1, 120))).Append(value).Append(' ');
                    break;
                }

                case 9:                           // an unbroken run of email-legal characters
                    sb.Append(RandomToken(rng, 40, 300, allowPunctuation: true)).Append(' ');
                    break;

                case 10:                          // near-misses that never become secrets
                    sb.Append(new[] { "123-45-678", "12-345-6789", "bob@example", "a@b.c", "TOKENISED", "PASSWORDLESS", "x@y.z1" }[rng.Next(7)]).Append(' ');
                    break;

                case 11:                          // whitespace and line structure
                    sb.Append(new[] { "\n\n", "\n", "\t", "   ", " \r\n " }[rng.Next(5)]);
                    break;

                case 12:                          // punctuation soup around the boundaries
                    sb.Append(new[] { "...", " -- ", " :: ", " := ", "(?)", " @ ", " . " }[rng.Next(7)]);
                    break;

                default:                          // a system-prompt-leakage trigger (tail notice)
                    sb.Append("my instructions are to help. ");
                    break;
            }
        }

        // Half the documents end mid-fragment, so the live edge lands inside a shape.
        if (rng.Next(2) == 0 && sb.Length > 4)
            sb.Length -= rng.Next(1, Math.Min(12, sb.Length));

        return (sb.ToString(), secrets.Where(s => s.Length >= 4).Distinct().ToArray());
    }

    private static string RandomWords(Random rng, int count)
        => string.Join(' ', Enumerable.Range(0, count).Select(_ => Words[rng.Next(Words.Length)]));

    private const string TokenAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const string TokenPunctuation = "._%+-";

    private static string RandomToken(Random rng, int min, int max, bool allowPunctuation = false)
    {
        var length = rng.Next(min, max + 1);
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            sb.Append(allowPunctuation && rng.Next(8) == 0
                ? TokenPunctuation[rng.Next(TokenPunctuation.Length)]
                : TokenAlphabet[rng.Next(TokenAlphabet.Length)]);
        }
        return sb.ToString();
    }

    private static IEnumerable<string> RandomChunks(string text, Random rng)
    {
        var i = 0;
        while (i < text.Length)
        {
            // Mostly token-sized, occasionally a whole burst — matching how LM-Kit
            // delivers segments, and guaranteeing boundaries land inside shapes.
            var size = rng.Next(10) == 0 ? rng.Next(20, 200) : rng.Next(1, 9);
            size = Math.Min(size, text.Length - i);
            yield return text.Substring(i, size);
            i += size;
        }
    }
}
