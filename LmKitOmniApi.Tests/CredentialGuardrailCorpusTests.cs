using System.Text.RegularExpressions;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.AI.Filters;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The output guardrail measured on BOTH sides of the trade it makes.
///
/// <para><b>Why this file exists.</b> Detection
/// (<see cref="PromptGuardService.AnalyzeOutputAsync"/>) and redaction
/// (<see cref="OutputGuardrailFilter"/>) used to be two hand-maintained pattern sets
/// per threat class, and they had drifted: the credential redactor made the ':'/'='
/// separator OPTIONAL while the detector made it MANDATORY. Redaction only runs once
/// detection has fired, so the narrower copy decided WHETHER anything was scrubbed —
/// <c>SECRET_KEY hunter2xyz</c> reached the user verbatim — and did so from unrelated
/// text elsewhere in the same answer. Closing that means WIDENING a security detector,
/// and a widened detector that fires on the word "password" in ordinary prose is a
/// product regression on every chat response. So both halves are pinned here: the leak
/// must be caught, and <see cref="Corpus"/> must survive.</para>
///
/// <para>Everything runs the REAL detector and the REAL filter — pure regex, no model —
/// so this pins shipped behaviour, not a mock of it.</para>
/// </summary>
public sealed class CredentialGuardrailCorpusTests
{
    // ── the patterns exactly as they shipped before this fix ─────────────────
    private static readonly Regex LegacyCredentialDetection = new(
        @"(?i)(API[-_\s]?KEY|SECRET[-_\s]?KEY|PASSWORD|TOKEN)\s*[:=]\s*\S+", RegexOptions.Compiled);
    private static readonly Regex LegacyBearerDetection = new(
        @"(?i)\bBEARER\s+\S+", RegexOptions.Compiled);
    private static readonly Regex LegacyCredentialRedaction = new(
        @"(?i)(API[-_\s]?KEY|SECRET[-_\s]?KEY|PASSWORD|TOKEN|BEARER)\s*[:=]?\s*\S+", RegexOptions.Compiled);
    private static readonly Regex LegacySsn = new(
        @"\b\d{3}[-.\s]?\d{2}[-.\s]?\d{4}\b", RegexOptions.Compiled);
    private static readonly Regex LegacyEmail = new(
        @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled);
    private static readonly Regex SystemPromptLeak = new(
        @"(?i)(system\s+prompt|my\s+instructions?\s+are|i\s+was\s+told\s+to|my\s+guidelines?\s+(say|are))",
        RegexOptions.Compiled);

    // ═════════════════════════════════════════════════════════════════════════
    // 1. The leak this fix exists to close
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Values carrying no ':' or '=' that are unmistakably secrets. Before the fix the
    /// keyword rows were never DETECTED, so never redacted — unless an unrelated
    /// credential in the same answer happened to carry a separator. The Bearer rows were
    /// covered by a second, separator-less detection pattern and are here to prove that
    /// coverage was not lost when the two patterns were folded into one.
    /// </summary>
    [Theory]
    [InlineData("SECRET_KEY hunter2xyz", "hunter2xyz")]
    [InlineData("API_KEY sk-live-51H8xQ2abcdef", "sk-live-51H8xQ2abcdef")]
    [InlineData("API KEY AKIAIOSFODNN7EXAMPLE", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("api-key ghp_16C7e42F292c6912E7710c838347Ae178B4a", "ghp_16C7e42F292c6912E7710c838347Ae178B4a")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("Bearer ya29.a0AfH6SMBx-1234567890", "ya29.a0AfH6SMBx-1234567890")]
    [InlineData("password Tr0ub4dor3", "Tr0ub4dor3")]
    [InlineData("TOKEN a1b2c3d4e5f6", "a1b2c3d4e5f6")]
    [InlineData("token 0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef")]
    [InlineData("Mật khẩu tạm thời của bạn là TOKEN a1b2c3d4e5f6", "a1b2c3d4e5f6")]
    public async Task CredentialWithoutASeparator_IsNowDetectedAndRedacted(string output, string secret)
    {
        var result = await NewFilter().OnOutputAsync(new AgentFilterContext { Output = output });

        Assert.DoesNotContain(secret, result.ProcessedContent);
        Assert.Contains("[REDACTED]", result.ProcessedContent);
    }

    /// <summary>
    /// The order dependence, gone. Before the fix the first of these two answers left
    /// "hunter2xyz" in the clear and the second removed it — the same secret, decided by
    /// a colon attached to some other credential in the same answer.
    /// </summary>
    [Fact]
    public async Task TheSameSecret_IsScrubbedRegardlessOfUnrelatedTextElsewhere()
    {
        var filter = NewFilter();

        var alone = await filter.OnOutputAsync(
            new AgentFilterContext { Output = "SECRET_KEY hunter2xyz" });
        var alongsideAColon = await filter.OnOutputAsync(
            new AgentFilterContext { Output = "TOKEN: abc123. SECRET_KEY hunter2xyz" });

        Assert.DoesNotContain("hunter2xyz", alone.ProcessedContent);
        Assert.DoesNotContain("hunter2xyz", alongsideAColon.ProcessedContent);
        Assert.True(LegacyCredentialRedaction.IsMatch("SECRET_KEY hunter2xyz"),
            "the legacy REDACTOR always matched this — it was the DETECTOR that did not, which is the whole bug");
        Assert.False(LegacyCredentialDetection.IsMatch("SECRET_KEY hunter2xyz"),
            "if the legacy detector matched this, the premise of the fix is wrong");
    }

    /// <summary>
    /// Credentials with an explicit separator, which must not have been narrowed. The
    /// "Bearer:" rows are new coverage: they fell between the two legacy detection
    /// patterns (the keyword pattern had no BEARER alternative; the BEARER pattern
    /// demanded whitespace right after the word), so folding both into one keyword set
    /// made this branch strictly wider.
    /// </summary>
    [Theory]
    [InlineData("API_KEY: sk-live-abcdef", "sk-live-abcdef")]
    [InlineData("API_KEY=super-secret-value", "super-secret-value")]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("SECRET_KEY  :  s3cr3tvalue", "s3cr3tvalue")]
    [InlineData("MYPASSWORD=letmein1", "letmein1")]
    [InlineData("Bearer: eyJhbGciOiJIUzI1NiJ9", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("Bearer=eyJhbGciOiJIUzI1NiJ9", "eyJhbGciOiJIUzI1NiJ9")]
    public async Task CredentialWithAnExplicitSeparator_IsStillRedacted(string output, string secret)
    {
        var result = await NewFilter().OnOutputAsync(new AgentFilterContext { Output = output });

        Assert.DoesNotContain(secret, result.ProcessedContent);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 2. The blast radius: ordinary answers
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Realistic assistant output in English and Vietnamese: prose about credentials,
    /// documentation-shaped text, code and config samples, and the identifiers,
    /// digests and reference numbers a coding assistant emits — the answers a widened
    /// guardrail is most likely to ruin. Entries listed in
    /// <see cref="MangledByTheShippedGuardrail"/> are the ones the product already
    /// mangles TODAY and still does; everything else must come through byte-identical.
    /// </summary>
    public static TheoryData<string> OrdinaryAnswers()
        => new(Corpus.Where(c => !MangledByTheShippedGuardrail.Contains(c)));

    [Theory]
    [MemberData(nameof(OrdinaryAnswers))]
    public async Task OrdinaryAnswer_SurvivesTheGuardrailUnchanged(string answer)
    {
        var result = await NewFilter().OnOutputAsync(new AgentFilterContext { Output = answer });

        Assert.Equal(answer, result.ProcessedContent);
        Assert.True(
            result.Warnings is null || result.Warnings.Count == 0,
            $"guardrail warned on an ordinary answer: {string.Join(" | ", result.Warnings ?? [])}");
    }

    /// <summary>
    /// The measurement, executable rather than asserted in prose. Over the same corpus:
    /// the pre-fix patterns rewrite 17 of 114 ordinary answers, the shipped ones rewrite
    /// 9 — and every one of those 9 is a case the pre-fix patterns already rewrote. The
    /// widened detector therefore introduces NO new false positive on this corpus while
    /// removing 8, which is what makes this a widening worth shipping.
    ///
    /// <para>Asserting set containment rather than a bare count keeps the claim true as
    /// the corpus grows: if a future pattern change mangles something new, this fails and
    /// names it.</para>
    /// </summary>
    [Fact]
    public async Task MeasuredOnOrdinaryAnswers_TheFixStrictlyReducesFalsePositives()
    {
        var filter = NewFilter();

        var legacyChanged = Corpus.Where(c => LegacyPipeline(c) != c).ToHashSet();
        var shippedChanged = new HashSet<string>();
        foreach (var answer in Corpus)
        {
            var result = await filter.OnOutputAsync(new AgentFilterContext { Output = answer });
            if (result.ProcessedContent != answer) shippedChanged.Add(answer);
        }

        // The headline numbers, pinned so the claim in S1-ROUND3.md stays checkable.
        // Corpus and counts live in this one file, so they move together by design.
        Assert.Equal(114, Corpus.Length);
        Assert.Equal(17, legacyChanged.Count);
        Assert.Equal(9, shippedChanged.Count);

        Assert.Equal(MangledByTheShippedGuardrail, shippedChanged);

        // No NEW false positive: everything the shipped guardrail still rewrites was
        // already rewritten before the fix.
        var introduced = shippedChanged.Except(legacyChanged).ToArray();
        Assert.True(introduced.Length == 0,
            "the fix introduced new false positives:\n  " + string.Join("\n  ", introduced));

        // And it removed a real slice of them.
        Assert.True(legacyChanged.Count > shippedChanged.Count,
            $"expected fewer false positives after the fix, got {legacyChanged.Count} -> {shippedChanged.Count}");
    }

    /// <summary>
    /// The other half of the same trade, measured: how much MORE the detector catches.
    /// The pre-fix detector saw 7 of these 20 secret-bearing answers; the shipped one
    /// sees 18. The two it still misses are the documented residual.
    /// </summary>
    [Fact]
    public async Task MeasuredOnSecretBearingAnswers_TheFixStrictlyIncreasesCoverage()
    {
        var filter = NewFilter();

        var legacyCaught = SecretBearing.Where(s => LegacyPipeline(s.Answer) != s.Answer).ToHashSet();
        var shippedCaught = new HashSet<(string Answer, string Value)>();
        foreach (var sample in SecretBearing)
        {
            var result = await filter.OnOutputAsync(new AgentFilterContext { Output = sample.Answer });
            if (!result.ProcessedContent.Contains(sample.Value, StringComparison.Ordinal))
                shippedCaught.Add(sample);
        }

        // Strictly wider: nothing the old detector caught is missed now.
        var lost = legacyCaught.Except(shippedCaught).ToArray();
        Assert.True(lost.Length == 0,
            "coverage was LOST for:\n  " + string.Join("\n  ", lost.Select(l => l.Answer)));

        Assert.True(shippedCaught.Count > legacyCaught.Count,
            $"expected wider coverage, got {legacyCaught.Count} -> {shippedCaught.Count}");
        Assert.Equal(SecretBearing.Length - DigitFreeResidual.Length, shippedCaught.Count);

        // The headline numbers, pinned alongside the false-positive ones.
        Assert.Equal(20, SecretBearing.Length);
        Assert.Equal(7, legacyCaught.Count);
        Assert.Equal(18, shippedCaught.Count);
    }

    /// <summary>
    /// The residual, stated rather than hidden: a separator-less value made only of
    /// letters is NOT treated as a credential. The old <c>\bBEARER\s+\S+</c> detector did
    /// catch this shape for BEARER alone — and fired on "Bearer authentication is
    /// required", "Bearer tokens travel in the Authorization header" and every other
    /// sentence about the scheme, each of which then had every keyword-plus-word span in
    /// the whole answer rewritten. Requiring a digit is what buys those answers back;
    /// this is the price, and it is deliberate.
    /// </summary>
    [Theory]
    [MemberData(nameof(DigitFreeResidualData))]
    public async Task SeparatorLessValueWithNoDigit_IsTheDocumentedResidual(string answer)
    {
        var result = await NewFilter().OnOutputAsync(new AgentFilterContext { Output = answer });

        Assert.Equal(answer, result.ProcessedContent);
    }

    public static TheoryData<string> DigitFreeResidualData() => new(DigitFreeResidual.Select(r => r.Answer));

    // ═════════════════════════════════════════════════════════════════════════
    // 3. The anti-drift invariant — the actual root cause
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The property the two hand-maintained pattern sets violated: detection and
    /// redaction must answer "is there a credential here?" identically, on every sample
    /// in both corpora. This is why
    /// <see cref="OutputGuardrailFilter.CredentialPatternText"/> is compiled by both
    /// sites instead of copied.
    /// </summary>
    [Fact]
    public async Task CredentialDetectionAndRedaction_AgreeOnEverySample()
    {
        var guard = new PromptGuardService(NullLogger<PromptGuardService>.Instance);

        foreach (var sample in Corpus.Concat(SecretBearing.Select(s => s.Answer)).Concat(EdgeShapes))
        {
            var analysis = await guard.AnalyzeOutputAsync(sample);
            var detected = analysis.Detections.Any(d => d.ThreatType == ThreatTypes.CredentialLeakage);
            var redacts = OutputGuardrailFilter.RedactCredentialContent(sample) != sample;

            Assert.True(detected == redacts,
                $"detector says {detected} but redactor says {redacts} for: {sample.Replace("\n", "\\n")}");
        }
    }

    /// <summary>The same invariant for the PII class, whose two patterns had also drifted
    /// (the detector's email TLD class was <c>[A-Z|a-z]</c>, which admits a literal '|').</summary>
    [Fact]
    public async Task PiiDetectionAndRedaction_AgreeOnEverySample()
    {
        var guard = new PromptGuardService(NullLogger<PromptGuardService>.Instance);

        foreach (var sample in Corpus.Concat(EdgeShapes))
        {
            var analysis = await guard.AnalyzeOutputAsync(sample);
            var detected = analysis.Detections.Any(d => d.ThreatType == ThreatTypes.PIILeakage);
            var redacts = OutputGuardrailFilter.RedactPiiContent(sample) != sample;

            Assert.True(detected == redacts,
                $"detector says {detected} but redactor says {redacts} for: {sample.Replace("\n", "\\n")}");
        }
    }

    /// <summary>
    /// Redaction stays idempotent: the streaming gate rebuilds its view over a growing
    /// prefix on every emit attempt, so a second pass over already-redacted text must be
    /// a no-op or the emitted prefix would drift from the full pass.
    /// </summary>
    [Theory]
    [InlineData("API_KEY: sk-live-abcdef")]
    [InlineData("SECRET_KEY hunter2xyz")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("Số 123-45-6789 và bob@example.com")]
    public void Redaction_IsIdempotent(string output)
    {
        var credOnce = OutputGuardrailFilter.RedactCredentialContent(output);
        Assert.Equal(credOnce, OutputGuardrailFilter.RedactCredentialContent(credOnce));

        var piiOnce = OutputGuardrailFilter.RedactPiiContent(output);
        Assert.Equal(piiOnce, OutputGuardrailFilter.RedactPiiContent(piiOnce));
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 4. Known issue #3 — patterns anchored so a secret glued to a word is missed
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The SSN half of known issue #3, fixed: a separated group glued to a word matched
    /// neither stage before. It must still not be claimed out of the middle of a longer
    /// number, which is what the digit-adjacency guards do.
    /// </summary>
    [Theory]
    [InlineData("123-45-6789The", true)]
    [InlineData("The123-45-6789", true)]
    [InlineData("Số bảo hiểm 123 45 6789x nhé", true)]
    [InlineData("abc123-45-6789", true)]
    [InlineData("123456789", true)]          // bare nine digits, isolated — covered before too
    [InlineData("x123456789y", false)]       // bare nine digits glued: still NOT an SSN
    [InlineData("1234567890", false)]        // ten digits: never was
    [InlineData("deadbeef123456789abc", false)]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", false)]
    [InlineData("550e8400-e29b-41d4-a716-446655440000", false)]
    [InlineData("2026-09-07 12:34:56", false)]
    public void GluedSsnShapes(string text, bool expectRedacted)
        => Assert.Equal(expectRedacted, OutputGuardrailFilter.RedactPiiContent(text) != text);

    /// <summary>
    /// The EMAIL half of known issue #3 was misdiagnosed. <c>bob@example.comThe</c>
    /// always matched — the greedy <c>[A-Za-z]{2,}</c> TLD simply swallowed the glued
    /// word, so the trailing <c>\b</c> held at the end of it. The genuine gap was glue to
    /// a DIGIT or an underscore, which no TLD class can absorb; dropping the trailing
    /// <c>\b</c> closes it.
    /// </summary>
    [Theory]
    [InlineData("bob@example.comThe", true, true)]
    [InlineData("Thebob@example.com", true, true)]
    [InlineData("bob@example.com123", false, true)]
    [InlineData("bob@example.com_x", false, true)]
    [InlineData("bob@example.c", false, false)]
    [InlineData("x@y.z1", false, false)]
    [InlineData("@example.com", false, false)]
    public void GluedEmailShapes(string text, bool redactedBefore, bool redactedNow)
    {
        Assert.Equal(redactedBefore, LegacyEmail.IsMatch(text));
        Assert.Equal(redactedNow, OutputGuardrailFilter.RedactPiiContent(text) != text);
    }

    /// <summary>
    /// The <c>\b</c> added after the credential keyword, which NARROWS both stages: the
    /// old redactor read <c>TOKENISED</c> as <c>TOKEN</c> + empty separator + <c>ISED</c>
    /// and rewrote it whenever anything else in the same answer tripped the detector.
    /// </summary>
    [Theory]
    [InlineData("TOKENISED values cannot be reversed. API_KEY: sk-live-abcdef")]
    [InlineData("PASSWORDLESS sign-in is available. API_KEY: sk-live-abcdef")]
    public async Task AWordCONTAININGAKeyword_IsNoLongerMangledWhenSomethingElseTripsTheDetector(string answer)
    {
        var result = await NewFilter().OnOutputAsync(new AgentFilterContext { Output = answer });

        Assert.Contains(answer.Split('.')[0], result.ProcessedContent);  // the prose half is intact
        Assert.DoesNotContain("sk-live-abcdef", result.ProcessedContent); // the secret half is not
        Assert.True(LegacyCredentialRedaction.IsMatch(answer.Split('.')[0]),
            "the legacy redactor mangled this prose; if it did not, this test proves nothing");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // helpers and corpora
    // ═════════════════════════════════════════════════════════════════════════

    private static OutputGuardrailFilter NewFilter()
        => new(
            new PromptGuardService(NullLogger<PromptGuardService>.Instance),
            NullLogger<OutputGuardrailFilter>.Instance);

    /// <summary>
    /// The guardrail exactly as it shipped before this change: legacy detection decides
    /// whether to redact, legacy redaction decides how much. Any single detection is
    /// enough — one detection scores 0.7 and <c>IsSafe</c> is <c>risk &lt; 0.7</c>.
    /// </summary>
    private static string LegacyPipeline(string text)
    {
        var system = SystemPromptLeak.IsMatch(text);
        var credential = LegacyCredentialDetection.IsMatch(text) || LegacyBearerDetection.IsMatch(text);
        var pii = LegacySsn.IsMatch(text) || LegacyEmail.IsMatch(text);
        if (!system && !credential && !pii) return text;

        var redacted = text;
        if (system) redacted += OutputGuardrailFilter.SystemPromptLeakageNotice;
        if (credential) redacted = LegacyCredentialRedaction.Replace(redacted, "$1: [REDACTED]");
        if (pii)
        {
            redacted = LegacySsn.Replace(redacted, "[SSN REDACTED]");
            redacted = LegacyEmail.Replace(redacted, "[EMAIL REDACTED]");
        }
        return redacted;
    }

    /// <summary>
    /// The nine corpus entries the shipped guardrail still rewrites. Every one of them is
    /// PRE-EXISTING behaviour that this change neither caused nor fixed: six are the SSN
    /// pattern claiming a nine-digit reference number (out of scope here — narrowing it
    /// would drop real SSNs), and three are the explicit-separator branch, unchanged from
    /// the old detection pattern, treating <c>KEY = value</c> in a code sample as an
    /// assignment. Listed rather than counted so a future regression names itself.
    /// </summary>
    private static readonly HashSet<string> MangledByTheShippedGuardrail =
    [
        "```csharp\nvar apiKey = configuration[\"AiModels:ApiKey\"]; // never hard-code\n```",
        "```bash\nexport API_KEY=$MY_SECRET   # read from your shell profile\n```",
        "```ts\nconst token = await getAccessToken();\nif (!token) throw new Error('no token');\n```",
        "Mã đơn hàng của bạn là 100244879.",
        "Invoice 2026-09-07-000123456 was issued yesterday.",
        "Serial number 123 45 6789 is printed on the label.",
        "Your order number is 100244879 and it ships on Tuesday.",
        "The file is 123456789 bytes, which is about 118 MB.",
        "Reference number 123-45-6789 was quoted in the ticket.",
    ];

    private static readonly string[] Corpus =
    [
        // ── English prose using the credential vocabulary, leaking nothing ──
        "Your password is stored as a bcrypt hash, never in plain text.",
        "Please enter your password below and click continue.",
        "Reset your password now if you think it was exposed.",
        "We never log the password anywhere in the request pipeline.",
        "Choose a password longer than twelve characters.",
        "The password requirements are documented in the onboarding guide.",
        "Password rotation happens automatically every ninety days.",
        "A password manager is the safest way to handle this.",
        "The password reset link is valid for one hour.",
        "Password strength is measured with zxcvbn before the form submits.",
        "Never email a password to a colleague; share it through the vault.",
        "If the password contains a space it is still accepted.",
        "The token limit for this model is 4096 tokens per request.",
        "The token expires after sixty minutes and is then refreshed silently.",
        "Each token represents roughly four characters of English text.",
        "The token bucket refills at a steady rate, so bursts are absorbed.",
        "Token counting happens before the request reaches the model.",
        "Token usage is billed per thousand tokens.",
        "An expired token returns 401 and the client refreshes silently.",
        "Bearer authentication is required on every endpoint under /api.",
        "Bearer tokens travel in the Authorization header, never in the URL.",
        "The bearer scheme is defined by RFC 6750.",
        "Bearer credentials must never be written to the application log.",
        "API key rotation policy is described in the operations runbook.",
        "Your API key never leaves the server, so the browser cannot read it.",
        "The API key belongs to the tenant, not to an individual user.",
        "An API key granting write access should be treated as a password.",
        "The API key header name is configurable per deployment.",
        "Secret key material is held in the data-protection keyring.",
        "The secret key rotates whenever the certificate is replaced.",
        "Passwordless sign-in uses a one-time link instead.",
        "The value is tokenised before it reaches the database.",
        "TOKENISED values cannot be reversed without the keyring.",
        "PASSWORDLESS authentication removes the shared secret entirely.",

        // ── documentation-shaped English ──
        "Store the API_KEY environment variable in your deployment secrets.",
        "Set PASSWORD in the environment rather than committing it.",
        "Read TOKEN from configuration instead of hard-coding it.",
        "The SECRET_KEY setting must be at least thirty-two bytes long.",
        "The API_KEY configuration section is read once at startup.",
        "Rename TOKEN to ACCESS_TOKEN so the two are easier to tell apart.",
        "Export API_KEY before running the integration suite.",
        "The PASSWORD column stores only a hash, never the value itself.",
        "Add TOKEN handling to the middleware pipeline.",
        "Prefer SECRET_KEY over an inline literal in appsettings.json.",
        "The API_KEY placeholder is replaced during deployment.",
        "Give TOKEN a descriptive name such as ACCESS_TOKEN or REFRESH_TOKEN.",
        "Set the environment variable API_KEY and restart the container.",
        "The header is written as `Authorization: Bearer <jwt>` with no quotes.",
        "Use `dotnet user-secrets set \"JwtSettings:SecretKey\" \"<value>\"` in development.",

        // ── a credential keyword followed by an identifier ──
        "The PASSWORD valueFrom block reads the value out of a Kubernetes secret.",
        "Rename TOKEN accessTokenLifetime to something shorter.",
        "The SECRET_KEY keyRingPath setting points at the mounted volume.",
        "Set API_KEY headerName if your gateway expects a different header.",
        "The column PASSWORD passwordHash was renamed in migration 12.",
        "TOKEN refreshTokenExpiry defaults to fourteen days.",
        "Use the API_KEY authenticationScheme registered in Program.cs.",
        "Bearer authenticationHandler is registered by AddJwtBearer().",
        "PASSWORD complexity rules are configured in IdentityOptions.",
        "The token isValid check happens before anything else.",
        "Set PASSWORD to the value of the database-administrator-account entry.",
        "The API_KEY configuration-section-name is read once at startup.",
        "TOKEN introspection-endpoint-address must be an absolute URI.",
        "Store SECRET_KEY somewhere-outside-of-source-control instead.",

        // ── code / config samples ──
        "```csharp\nvar apiKey = configuration[\"AiModels:ApiKey\"]; // never hard-code\n```",
        "```json\n{ \"JwtSettings\": { \"SecretKey\": \"<set-in-environment>\" } }\n```",
        "```bash\nexport API_KEY=$MY_SECRET   # read from your shell profile\n```",
        "```yaml\nenv:\n  - name: PASSWORD\n    valueFrom:\n      secretKeyRef:\n        name: db-credentials\n```",
        "```http\nGET /api/chat HTTP/1.1\nAuthorization: Bearer <token>\n```",
        "```csharp\nrequest.Headers.Authorization = new AuthenticationHeaderValue(\"Bearer\", accessToken);\n```",
        "```ts\nconst token = await getAccessToken();\nif (!token) throw new Error('no token');\n```",
        "```csharp\npublic string Password { get; set; }\npublic string TokenHash { get; set; }\n```",
        "```csharp\nrecord Login(string Email, string Password);\n```",

        // ── Vietnamese ──
        "Mật khẩu của bạn được lưu dưới dạng băm, không lưu bản rõ.",
        "Vui lòng nhập password rồi bấm tiếp tục.",
        "Token hết hạn sau sáu mươi phút và sẽ được làm mới tự động.",
        "Giới hạn token cho mô hình này là bốn nghìn token mỗi yêu cầu.",
        "API key phải được lưu trong biến môi trường, không đưa vào mã nguồn.",
        "Khoá bí mật (secret key) nằm trong kho khoá của hệ thống.",
        "Bearer token được gửi trong header Authorization.",
        "Đổi password định kỳ là một thói quen tốt.",
        "Bạn nên dùng trình quản lý password thay vì ghi ra giấy.",
        "Hệ thống không bao giờ ghi log password của người dùng.",
        "Token truy cập sẽ được làm mới ngầm khi hết hạn.",
        "Nếu quên password, hãy dùng liên kết đặt lại được gửi qua email.",
        "Tôi đã cập nhật tệp cấu hình và chạy lại bộ kiểm thử.",
        "Bản dựng hoàn tất sau 4,2 giây và không có cảnh báo nào.",
        "Bạn có thể liên hệ bộ phận hỗ trợ trong giờ hành chính.",
        "Mã đơn hàng của bạn là 100244879.",
        "Số điện thoại hỗ trợ: 1900 1234.",

        // ── hashes, ids and numbers a coding assistant emits ──
        "The build succeeded in 4.2 seconds with no warnings.",
        "Commit e1964a8 merged the grounding tune-kit into master.",
        "The SHA-256 digest is 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08.",
        "The commit is e1964a8f2b3c4d5e6f708192a3b4c5d6e7f80912.",
        "The trace id is 4bf92f3577b34da6a3ce929d0e0e4736.",
        "Correlation id 550e8400-e29b-41d4-a716-446655440000 appears in every log line.",
        "Run 8f14e45fceea167a5a36dedd4bea2543 finished in 12 seconds.",
        "The build number is 20260907.3 and the artefact is 41 MB.",
        "Latency was 123.45 ms at the 99th percentile.",
        "Port 8080 forwards to 127.0.0.1:5000 inside the container.",
        "Invoice 2026-09-07-000123456 was issued yesterday.",
        "The phone number on file is 415-555-0132.",
        "Serial number 123 45 6789 is printed on the label.",
        "Your order number is 100244879 and it ships on Tuesday.",
        "The file is 123456789 bytes, which is about 118 MB.",
        "Reference number 123-45-6789 was quoted in the ticket.",
        "Call +1 415 555 0132 or write to the address in the footer.",
        "Version 1.2.3 fixes the crash reported in issue 4821.",

        // ── plain answers, no security vocabulary at all ──
        "The endpoint returns 204 when the resource is already gone.",
        "I have added a migration and updated the DbContext accordingly.",
        "Here is a shorter version of the paragraph you asked about.",
        "The chart shows a steady decline from March through August.",
        "The migration adds one column and takes about 30 seconds on a warm database.",
        "The test suite passes with 1218 tests and 9 skipped.",
        "Xin chào, tôi có thể giúp gì cho bạn hôm nay?",
    ];

    private static readonly (string Answer, string Value)[] SecretBearing =
    [
        // separator-less — the leak this fix closes
        ("SECRET_KEY hunter2xyz", "hunter2xyz"),
        ("API_KEY sk-live-51H8xQ2abcdef", "sk-live-51H8xQ2abcdef"),
        ("API KEY AKIAIOSFODNN7EXAMPLE", "AKIAIOSFODNN7EXAMPLE"),
        ("Bearer eyJhbGciOiJIUzI1NiJ9", "eyJhbGciOiJIUzI1NiJ9"),
        ("password Tr0ub4dor3", "Tr0ub4dor3"),
        ("TOKEN a1b2c3d4e5f6", "a1b2c3d4e5f6"),
        ("token 0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef"),
        ("Bearer ya29.a0AfH6SMBx-1234567890", "ya29.a0AfH6SMBx-1234567890"),
        ("api-key ghp_16C7e42F292c6912E7710c838347Ae178B4a", "ghp_16C7e42F292c6912E7710c838347Ae178B4a"),
        ("Mật khẩu tạm thời của bạn là TOKEN a1b2c3d4e5f6", "a1b2c3d4e5f6"),
        ("Bearer YWxhZGRpbjpvcGVuc2VzYW1l1", "YWxhZGRpbjpvcGVuc2VzYW1l1"),
        // separator-less and digit-free — the documented residual
        ("password correcthorsebatterystaple", "correcthorsebatterystaple"),
        ("SECRET_KEY abcdefghijklmnopqrstuvwx", "abcdefghijklmnopqrstuvwx"),
        // explicit separator — covered before the fix, must stay covered
        ("API_KEY: sk-live-abcdef", "sk-live-abcdef"),
        ("password=hunter2", "hunter2"),
        ("SECRET_KEY=s3cr3tvalue", "s3cr3tvalue"),
        ("Khoá của bạn: API_KEY sk-live-9fA2bQ7", "sk-live-9fA2bQ7"),
        ("MYPASSWORD=letmein1", "letmein1"),
        // "Bearer" plus a separator — between the two legacy patterns, new coverage
        ("Bearer: eyJhbGciOiJIUzI1NiJ9", "eyJhbGciOiJIUzI1NiJ9"),
        ("Bearer=eyJhbGciOiJIUzI1NiJ9", "eyJhbGciOiJIUzI1NiJ9"),
    ];

    private static readonly (string Answer, string Value)[] DigitFreeResidual =
    [
        ("password correcthorsebatterystaple", "correcthorsebatterystaple"),
        ("SECRET_KEY abcdefghijklmnopqrstuvwx", "abcdefghijklmnopqrstuvwx"),
    ];

    /// <summary>Boundary shapes that exist to make the agreement invariants non-vacuous.</summary>
    private static readonly string[] EdgeShapes =
    [
        "TOKENISED", "PASSWORDLESS", "MYPASSWORD=letmein1", "TOKEN", "Bearer", "PASSWORD ",
        "TOKEN: abc123. SECRET_KEY hunter2xyz", "API_KEY:", "SECRET_KEY  :  s3cr3tvalue",
        "123-45-6789The", "bob@example.com123", "bob@example.com_x", "a@b.co", "x@y.z1",
        "TOKEN abc1", "TOKEN abcdefg1", "TOKEN abcdefgh1", "PASSWORD 12345678",
    ];
}
