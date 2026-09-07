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
    // A real JWT header segment. It used to read "Bearer eyJhbGciOi", which the old
    // \bBEARER\s+\S+ detector matched because ANY word after "Bearer" was a credential
    // to it — the same rule that rewrote "Bearer authentication is required" in ordinary
    // answers. The shared pattern asks the value to look like a secret (8+ characters,
    // at least one digit), which every real base64url token satisfies and that
    // hand-truncated stub did not. The residual is pinned in
    // CredentialGuardrailCorpusTests.SeparatorLessValueWithNoDigit_IsTheDocumentedResidual.
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("password=hunter2")]
    [InlineData("SECRET_KEY hunter2xyz")]
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
    /// A credential whose VALUE is itself a PII shape — the case that decides WHERE the
    /// unlatched credential boundary is measured.
    ///
    /// <para>In <c>raw</c>, <c>TOKEN 123-45-6789</c> is a credential span (the
    /// separator-less branch: 11 value characters, digits among them). In a view where
    /// PII has already latched it reads <c>TOKEN [SSN REDACTED]</c>, which is NOT a
    /// credential span — <c>'['</c> is not a value character. The full pass redacts
    /// credentials FIRST (detection order in <c>PromptGuardService.LeakagePatterns</c>),
    /// so its answer is <c>TOKEN: [REDACTED]</c>. A gate that measured the credential
    /// boundary on the view would have streamed <c>TOKEN [SSN REDACTED]</c> and then
    /// diverged, costing the user the tail of the answer. Measuring it on <c>raw</c> —
    /// the input the credential transform actually receives — is what keeps the two
    /// equal.</para>
    /// </summary>
    [Theory]
    [InlineData("TOKEN 123-45-6789")]
    [InlineData("PASSWORD 123.45.6789")]
    [InlineData("API_KEY 987 65 4321")]
    [InlineData("PASSWORD abcdefg1@mail.example.com")]
    [InlineData("Bearer nguyenvan1@example.com")]
    public async Task CredentialWhoseValueIsAlsoAPiiShape_NeverDiverges(string span)
    {
        // The leading address latches PII EARLY, so the span below is reached with the
        // view already carrying [SSN REDACTED] / [EMAIL REDACTED] substitutions.
        var document = "lien he bob@example.com. " + Prose(20) + " " + span + ". " + Prose(20);

        await AssertContractAsync(0, document, RandomChunks(document, new Random(11)).ToArray(), []);
    }

    /// <summary>
    /// The stall this gate's emission boundary was rebuilt to remove.
    ///
    /// <para>The unlatched credential cap used to anchor on a KEYWORD-only pattern, and
    /// that cap is recomputed from the emitted length on every attempt — so a single
    /// unlatched keyword pinned emission at that word for the rest of the answer. An
    /// answer that merely DISCUSSES passwords or tokens contains no credential span, never
    /// latches the class, and therefore stopped streaming at the word and arrived in one
    /// lump on the end-of-stream flush — undoing, for a whole class of answers, the thing
    /// the rebuilt hold rule bought (first chunk at raw character 16 instead of 544).</para>
    ///
    /// <para>The assertion is a LATENCY one on purpose: every other test in this file
    /// would pass just as well against a gate that streamed nothing at all.</para>
    /// </summary>
    [Theory]
    [InlineData("Bạn nên đổi password định kỳ và không chia sẻ với ai.")]
    [InlineData("A bearer token is only a header value, never a secret in itself.")]
    [InlineData("Never store a password in plain text.")]
    [InlineData("You should rotate the API key every quarter.")]
    [InlineData("The secret key lives in the vault, and the token expires hourly.")]
    public async Task ProseThatMerelyDiscussesCredentials_KeepsStreaming(string sentence)
    {
        var document = sentence + " " + Prose(60);

        var guard = new PromptGuardService(NullLogger<PromptGuardService>.Instance);
        var filter = new OutputGuardrailFilter(guard, NullLogger<OutputGuardrailFilter>.Instance);
        var final = (await filter.OnOutputAsync(new AgentFilterContext { Output = document }))
            .ProcessedContent ?? string.Empty;

        // Nothing here is a credential. If this ever fails the fixture has grown a real
        // secret, and the assertion below would be measuring a redaction, not a stall.
        Assert.Equal(document, final);

        var gate = new StreamingGuardrailGate(guard);
        foreach (var c in document)
            await gate.AppendAndTryEmitAsync(c.ToString(), CancellationToken.None);

        var emitted = gate.EmittedText;
        Assert.True(final.StartsWith(emitted, StringComparison.Ordinal), "emitted text is not a prefix.");

        // What is left for the end-of-stream flush is the hold window plus at most one
        // emit stride — tens of characters. A gate pinned at the keyword leaves
        // everything after it behind, which for these fixtures is several hundred.
        var withheld = final.Length - emitted.Length;
        Assert.True(
            withheld <= 40,
            $"{withheld} of {final.Length} characters never streamed; emission stopped after "
            + Trim(emitted[Math.Max(0, emitted.Length - 60)..]));
    }

    /// <summary>
    /// Finding surfaced by the randomized corpus, since FIXED — this test used to be
    /// called <c>CredentialsWithoutASeparatorAreNeverDetected</c> and PINNED the leak.
    ///
    /// <para><see cref="OutputGuardrailFilter.CredentialRedactionPattern"/> made the
    /// ':'/'=' separator optional while <c>PromptGuardService</c>'s detection pattern
    /// made it mandatory, and the filter only redacts once the detector has fired. A
    /// credential written with a plain space ("SECRET_KEY hunter2xyz") was therefore
    /// delivered to the user in the clear — UNLESS some unrelated credential in the same
    /// answer happened to carry a colon, at which point the wider redaction pattern
    /// scrubbed it after all. The same secret leaked or did not depending on text
    /// elsewhere in the answer.</para>
    ///
    /// <para>Both stages now compile
    /// <see cref="OutputGuardrailFilter.CredentialPatternText"/>, so this asserts what
    /// the product must do: scrub the value either way, and identically. The
    /// false-positive cost of the wider detector is measured in
    /// <c>CredentialGuardrailCorpusTests</c>.</para>
    /// </summary>
    [Fact]
    public async Task CredentialsWithoutASeparatorAreScrubbedRegardlessOfTheRestOfTheAnswer()
    {
        var guard = new PromptGuardService(NullLogger<PromptGuardService>.Instance);
        var filter = new OutputGuardrailFilter(guard, NullLogger<OutputGuardrailFilter>.Instance);

        var spaceOnly = await filter.OnOutputAsync(new AgentFilterContext { Output = "SECRET_KEY hunter2xyz" });
        Assert.DoesNotContain("hunter2xyz", spaceOnly.ProcessedContent);

        // ...and a colon elsewhere in the answer no longer changes that outcome.
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
    /// product never redacts (a separator-less value shorter than 8 characters or
    /// carrying no digit; see <c>CredentialGuardrailCorpusTests</c> for where that line
    /// is drawn and why) is not something the gate is allowed to withhold. Pass
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
            switch (rng.Next(17))
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

                case 13:                          // prose that merely TALKS about credentials
                    // No value follows the keyword, so nothing here is a credential span and
                    // the class never latches off it. This is the shape whose keyword used to
                    // pin emission for the rest of the answer; carrying it in the corpus keeps
                    // the prefix contract honest for documents that mix it with real secrets.
                    sb.Append(new[]
                    {
                        "never store a password in plain text. ",
                        "a bearer token is only a header value. ",
                        "you should rotate the API key every quarter. ",
                        "the secret key lives in the vault. ",
                        "đổi password định kỳ và không chia sẻ. "
                    }[rng.Next(5)]);
                    break;

                case 14:                          // keyword + SSN-shaped value, NO separator
                {
                    // The credential span and a PII span overlap. The full pass redacts
                    // credentials first, so the whole span becomes "KEYWORD: [REDACTED]" and
                    // the SSN never reaches the SSN pattern — while a view that had already
                    // substituted [SSN REDACTED] shows no credential at all. The gate has to
                    // agree with the full pass, not with the view.
                    var keyword = new[] { "TOKEN", "PASSWORD", "API_KEY", "Bearer" }[rng.Next(4)];
                    var sep = new[] { "-", ".", " " }[rng.Next(3)];
                    var ssn = $"{rng.Next(100, 1000)}{sep}{rng.Next(10, 100)}{sep}{rng.Next(1000, 10000)}";
                    secrets.Add(ssn);
                    sb.Append(keyword).Append(' ').Append(ssn).Append(". ");
                    break;
                }

                case 15:                          // keyword + email-shaped value, NO separator
                {
                    var keyword = new[] { "PASSWORD", "TOKEN", "SECRET_KEY", "Bearer" }[rng.Next(4)];
                    // 8+ value characters with a digit, so the separator-less branch fires,
                    // and an '@' host so the email pattern claims the same span.
                    var email = $"{RandomToken(rng, 7, 14)}1@{RandomToken(rng, 3, 9)}.example.com";
                    secrets.Add(email);
                    sb.Append(keyword).Append(' ').Append(email).Append(". ");
                    break;
                }

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
