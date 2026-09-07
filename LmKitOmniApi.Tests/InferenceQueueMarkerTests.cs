using System.Diagnostics;
using System.Reflection;
using LmKitOmniApi.Application.Chat.Handlers;
using LmKitOmniApi.Services;
using Microsoft.Extensions.Configuration;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The queue-position notice is a NEW producer on the in-band SSE marker channel, so it
/// inherits the contract <see cref="ProtocolMarkerStreamTests"/> exists to defend: a marker
/// must be terminated by a REAL newline. Both strippers are line-anchored — the server's
/// <c>StreamChatCommandHandler.StripProtocolMarkers</c> uses
/// <c>\[THINKING\]:[^\n\r]+[\n\r]*</c> — so a marker emitted with the two characters
/// backslash + 'n' (what a non-verbatim C# literal <c>"…\\n"</c> puts on the wire) leaves no
/// newline, and <c>[^\n\r]+</c> then runs greedily straight through the answer behind it. A
/// one-paragraph answer renders EMPTY on history reload and in ShareView, and the server's
/// own stripped view — which decides whether a partial answer is worth persisting on client
/// abort — comes back empty and discards it.
///
/// These tests drive the REAL notice out of the REAL queue under real contention rather than
/// asserting against a copy of the format string.
/// </summary>
public class InferenceQueueMarkerTests
{
    private static readonly MethodInfo StripProtocolMarkersMethod =
        typeof(StreamChatCommandHandler).GetMethod(
            "StripProtocolMarkers", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "StreamChatCommandHandler.StripProtocolMarkers not found — the marker-stripping "
            + "contract this test pins has moved; update the test rather than deleting it.");

    private static string StripProtocolMarkers(string raw) =>
        (string)StripProtocolMarkersMethod.Invoke(null, new object?[] { raw })!;

    /// A realistic one-paragraph answer: no internal newline, which is exactly the shape a
    /// line-anchored stripper destroys when the preceding marker has no real newline.
    private const string OneParagraphAnswer =
        "Hà Nội là thủ đô của Việt Nam và là trung tâm chính trị, văn hoá của cả nước, "
        + "nằm bên bờ sông Hồng ở khu vực đồng bằng Bắc Bộ.";

    /// <summary>
    /// Collects queue notices emitted by the shipping code while the single chat permit is
    /// genuinely held by someone else.
    /// </summary>
    private static async Task<List<string>> CaptureRealQueueNoticesAsync(int count)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SemaphoreLimits:Chat"] = "1",
                ["InferenceQueue:Chat:MaxWaitSeconds"] = "0",
                ["InferenceQueue:Chat:NoticeAfterMilliseconds"] = "1",
                ["InferenceQueue:Chat:NoticeIntervalSeconds"] = "1",
            })
            .Build();
        using var manager = new LmModelManager(configuration);

        var holder = await manager.AcquireChatInferenceAsync();
        var admission = manager.BeginChatInference();
        try
        {
            var notices = new List<string>();
            await foreach (var notice in admission.WaitForTurnAsync())
            {
                notices.Add(notice);
                if (notices.Count == count) break;
            }

            Assert.Equal(count, notices.Count);
            return notices;
        }
        finally
        {
            await admission.DisposeAsync();
            await holder.DisposeAsync();
        }
    }

    // ── 1. The contract every marker producer must hold ──────────────────────

    [Fact]
    public async Task QueueNotice_UsesTheExistingThinkingChannel_AndEndsWithARealNewline()
    {
        var notices = await CaptureRealQueueNoticesAsync(2);

        foreach (var notice in notices)
        {
            // Reusing [THINKING] is the whole point: it is already stripped by the server and
            // already rendered by the client, so a queued turn needs no new marker, no new
            // stripper and no client change.
            Assert.StartsWith("[THINKING]:", notice, StringComparison.Ordinal);

            Assert.EndsWith("\n", notice, StringComparison.Ordinal);
            Assert.DoesNotContain(@"\n", notice, StringComparison.Ordinal);

            // Exactly one line: an interior newline would split the marker and leave its tail
            // in the visible answer.
            Assert.Equal(1, notice.Count(character => character == '\n'));
        }
    }

    [Fact]
    public async Task QueueNotice_ReportsPositionAndElapsedWait()
    {
        var notices = await CaptureRealQueueNoticesAsync(1);

        Assert.Contains("vị trí 1/1", notices[0], StringComparison.Ordinal);
        Assert.Contains("đã chờ", notices[0], StringComparison.Ordinal);
    }

    // ── 2. The answer survives the shipping stripper ─────────────────────────

    [Fact]
    public async Task ServerStripper_KeepsTheAnswer_WhenQueueNoticesPrecedeIt()
    {
        var notices = await CaptureRealQueueNoticesAsync(3);
        var stream = string.Concat(notices) + OneParagraphAnswer;

        var stripped = StripProtocolMarkers(stream);

        Assert.Equal(OneParagraphAnswer, stripped);
        Assert.DoesNotContain("[THINKING]", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("vị trí", stripped, StringComparison.Ordinal);
    }

    /// <summary>
    /// The queue notices are emitted BEFORE the orchestrator's existing status prelude, so
    /// pin the realistic full stream rather than the notices alone.
    /// </summary>
    [Fact]
    public async Task ServerStripper_KeepsTheAnswer_AcrossQueueNoticesAndTheExistingPrelude()
    {
        var notices = await CaptureRealQueueNoticesAsync(2);
        var stream = "[Agent invoked: Hermes]\n"
            + "[THINKING]: 🛡️ Kiểm tra bảo mật đầu vào...\n"
            + "[THINKING]: ✅ Đầu vào an toàn\n"
            + string.Concat(notices)
            + "[THINKING]: ✍️ Đang tổng hợp và tạo câu trả lời...\n"
            + "[WEB_SEARCH]: {\"title\":\"Nguồn A\",\"url\":\"https://example.com/a\"}\n"
            + "[REASONING]: cân nhắc các nguồn rồi tổng hợp\n"
            + OneParagraphAnswer;

        Assert.Equal(OneParagraphAnswer, StripProtocolMarkers(stream));
    }

    // ── 3. Negative control: the bug this suite exists to prevent ────────────

    /// <summary>
    /// The same notice text with the two-character escape instead of a newline — what a
    /// non-verbatim <c>"…\\n"</c> literal would emit — swallows the answer whole. This is the
    /// failure this repo has already shipped twice, reproduced here for the queue marker so
    /// the positive tests above cannot silently become vacuous.
    /// </summary>
    [Fact]
    public async Task NegativeControl_TheSameNoticeWithALiteralBackslashN_SwallowsTheAnswer()
    {
        var notice = (await CaptureRealQueueNoticesAsync(1))[0];
        var broken = notice.TrimEnd('\n') + @"\n" + OneParagraphAnswer;

        Assert.Contains(@"\n", broken, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', broken);

        var stripped = StripProtocolMarkers(broken);

        Assert.True(
            string.IsNullOrEmpty(stripped),
            $"expected the line-anchored stripper to swallow everything, got: '{stripped}'");
    }

    // ── 4. The rejection message is user-facing, not a marker ────────────────

    /// <summary>
    /// A refusal is shown to the user as ordinary visible text (the idiom AgentOrchestrator
    /// already uses for a blocked input: <c>yield return $"⚠️ {reason}"</c>), so it must
    /// survive stripping intact and carry no marker syntax of its own.
    /// </summary>
    [Fact]
    public async Task RejectionMessage_IsPlainVisibleText_ThatSurvivesStripping()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SemaphoreLimits:Chat"] = "1",
                ["InferenceQueue:Chat:MaxWaitSeconds"] = "1",
                ["InferenceQueue:Chat:NoticeAfterMilliseconds"] = "1",
            })
            .Build();
        using var manager = new LmModelManager(configuration);

        var holder = await manager.AcquireChatInferenceAsync();
        InferenceQueueRejectedException? rejection;
        await using (var admission = manager.BeginChatInference())
        {
            await foreach (var _ in admission.WaitForTurnAsync())
            {
                // Drain the notices until the bound is hit.
            }

            rejection = admission.Rejection;
            Assert.False(admission.IsGranted);
        }

        await holder.DisposeAsync();

        Assert.NotNull(rejection);
        Assert.Equal(InferenceQueueRejectionReason.WaitTimeout, rejection!.Reason);

        var visible = $"⚠️ {rejection.Message}";
        Assert.DoesNotContain('[', visible);
        Assert.Equal(visible, StripProtocolMarkers(visible));
    }

    /// <summary>
    /// The streaming path must be able to refuse without throwing: C# forbids a
    /// <c>catch</c> around a <c>yield return</c>, so a wait that threw could not be handled
    /// inside <c>AgentOrchestrator.StreamProcessQueryAsync</c> and would escape to the
    /// controller, which turns every exception into "[ERROR]: Unable to generate a response."
    /// </summary>
    [Fact]
    public async Task StreamingAdmission_ReportsRejection_WithoutThrowing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SemaphoreLimits:Chat"] = "1",
                ["InferenceQueue:Chat:MaxWaitSeconds"] = "1",
                ["InferenceQueue:Chat:NoticeAfterMilliseconds"] = "600000",
            })
            .Build();
        using var manager = new LmModelManager(configuration);

        var holder = await manager.AcquireChatInferenceAsync();
        var elapsed = Stopwatch.StartNew();

        await using (var admission = manager.BeginChatInference())
        {
            var notices = new List<string>();
            await foreach (var notice in admission.WaitForTurnAsync())
                notices.Add(notice);
            elapsed.Stop();

            Assert.Empty(notices);
            Assert.NotNull(admission.Rejection);
            Assert.False(admission.IsGranted);
        }

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(30),
            $"the bounded wait did not end promptly ({elapsed.ElapsedMilliseconds}ms)");

        await holder.DisposeAsync();
    }
}
