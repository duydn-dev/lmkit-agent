using System.Reflection;
using System.Text;
using LmKitOmniApi.Application.Chat.Handlers;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.AI.ComputerUse;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Regression tests for the in-band SSE marker protocol, pinning the ONE property every
/// producer of a <c>[THINKING]</c> marker must hold: the marker is terminated by a REAL
/// newline.
///
/// Why it matters: both strippers are line-anchored — the server's
/// <c>StreamChatCommandHandler.StripProtocolMarkers</c>
/// (<c>\[THINKING\]:[^\n\r]+[\n\r]*</c>) and the client's
/// <c>parseStoredAssistantContent</c>. A marker emitted with the two-character escape
/// (backslash + 'n', as a non-verbatim C# literal <c>"…\\n"</c> produces) puts NO newline
/// on the wire, so <c>[^\n\r]+</c> runs greedily straight through the answer that follows
/// it. A one-paragraph answer then renders EMPTY on history reload and in the public
/// ShareView, and the server's own stripped view — which decides whether a partial answer
/// is worth persisting on client abort — comes back empty, discarding it.
///
/// The tests below cover both ends of that contract: the stripper's behaviour given each
/// wire form, and a REAL producer (<see cref="ComputerUseAgent"/>, driven hermetically)
/// whose emitted markers must survive it.
/// </summary>
public class ProtocolMarkerStreamTests
{
    // The server-side stripper is a private static of the chat handler; it is invoked
    // through reflection so this test exercises the SHIPPING implementation rather than a
    // copy of its regexes.
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

    // ── 1. The contract: real newlines → the answer survives the server stripper ──

    [Fact]
    public void ServerStripper_KeepsOneParagraphAnswer_WhenThinkingMarkersEndWithRealNewline()
    {
        // The orchestrator's status prelude, in emission order, terminated the way the
        // fixed AgentOrchestrator emits it.
        var stream = new StringBuilder()
            .Append("[THINKING]: 🛡️ Kiểm tra bảo mật đầu vào...\n")
            .Append("[THINKING]: ✅ Đầu vào an toàn\n")
            .Append("[THINKING]: 🧠 Tìm kiếm ký ức liên quan...\n")
            .Append("[THINKING]: 🧠 Không có ký ức liên quan\n")
            .Append("[THINKING]: 📋 Khởi tạo LM-Kit ReAct agent với công cụ có cấu trúc...\n")
            .Append("[THINKING]: ✍️ Đang tổng hợp và tạo câu trả lời...\n")
            .Append(OneParagraphAnswer)
            .ToString();

        var stripped = StripProtocolMarkers(stream);

        Assert.Equal(OneParagraphAnswer, stripped);
        Assert.DoesNotContain("[THINKING]", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerStripper_KeepsAnswer_AcrossTheFullMarkerMix()
    {
        var stream = "[Agent invoked: Hermes]\n"
            + "[THINKING]: 🛡️ Kiểm tra bảo mật đầu vào...\n"
            + "[WEB_SEARCH]: {\"title\":\"Nguồn A\",\"url\":\"https://example.com/a\"}\n"
            + "[REASONING]: cân nhắc các nguồn rồi tổng hợp\n"
            + OneParagraphAnswer;

        var stripped = StripProtocolMarkers(stream);

        Assert.Equal(OneParagraphAnswer, stripped);
    }

    // ── 2. Negative control: the bug this suite exists to prevent ──

    [Fact]
    public void ServerStripper_SwallowsWholeAnswer_WhenThinkingMarkerEndsWithLiteralBackslashN()
    {
        // Exactly what a non-verbatim C# literal "…\\n" put on the wire: the two
        // characters backslash + 'n', NOT a newline. Note the doubled backslash here is a
        // C# escape, so `broken` really does contain backslash + 'n'.
        var broken = "[THINKING]: 🛡️ Kiểm tra bảo mật đầu vào...\\n" + OneParagraphAnswer;
        Assert.Contains(@"\n", broken, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', broken);

        var stripped = StripProtocolMarkers(broken);

        // [^\n\r]+ ran through the answer: a genuine answer is gone. This is the failure
        // mode behind the empty history/ShareView render and the discarded partial answer.
        Assert.True(string.IsNullOrEmpty(stripped),
            $"expected the line-anchored stripper to swallow everything, got: '{stripped}'");
    }

    // ── 3. A real producer: the [WEB_SEARCH] marker AgentOrchestrator now emits ──
    //
    // Until this round nothing in the backend wrote this marker: the only [WEB_SEARCH]
    // in the API was the regex that strips it, while the client shipped a complete
    // consumption path (chatSse → useChatStream → the "Read N web pages" chip and the
    // reference drawer in ChatView/ShareView). The orchestrator now emits it from the
    // URLs search_web actually returned, and it must hold the same newline contract
    // every other marker does.

    [Fact]
    public void WebSearchMarker_EndsWithARealNewline_AndTheAnswerSurvivesStripping()
    {
        var marker = AgentOrchestrator.FormatWebSearchMarker(
            new[] { "https://example.com/a", "https://example.com/b" });

        Assert.DoesNotContain(@"\n", marker, StringComparison.Ordinal);
        Assert.EndsWith("\n", marker, StringComparison.Ordinal);
        Assert.Equal("[WEB_SEARCH]:https://example.com/a|https://example.com/b\n", marker);

        var stripped = StripProtocolMarkers(marker + OneParagraphAnswer);

        Assert.Equal(OneParagraphAnswer, stripped);
        Assert.DoesNotContain("[WEB_SEARCH]", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void WebSearchMarker_SurvivesTheRealEmissionOrder_AfterAFileMarker()
    {
        // [FILE:] markers carry no newline of their own and are emitted immediately
        // before the citation marker, so the two are adjacent on the wire.
        var stream = "[THINKING]: ✅ LM-Kit ReAct hoàn tất sau 2 inference(s)\n"
            + "[FILE:{\"id\":\"c.png\",\"name\":\"chart.png\",\"contentType\":\"image/png\",\"size\":9}]"
            + AgentOrchestrator.FormatWebSearchMarker(new[] { "https://example.com/a" })
            + "[THINKING]: ✍️ Đang tổng hợp và tạo câu trả lời...\n"
            + OneParagraphAnswer;

        var stripped = StripProtocolMarkers(stream);

        Assert.Contains(OneParagraphAnswer, stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("[WEB_SEARCH]", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("[THINKING]", stripped, StringComparison.Ordinal);
    }

    /// <summary>
    /// The client splits the marker payload on '|' and reads each element as a URL, so
    /// a hit whose URL carries the framing character (or a newline) would silently
    /// become two bogus references — or terminate the marker early and leave the rest
    /// of it rendered as prose. Those hits are dropped, and so is anything that is not
    /// an absolute http/https URL (mirroring the client's isSafeWebUrl allowlist).
    /// </summary>
    [Fact]
    public void ExtractWebReferences_KeepsOnlyUrlsThatAreSafeAndCannotCorruptTheMarker()
    {
        var observation = """
            [
              {"url":"https://ok.example/a","title":"A","snippet":"…"},
              {"url":"http://ok.example/b","title":"B","snippet":"…"},
              {"url":"javascript:alert(1)","title":"XSS","snippet":"…"},
              {"url":"data:text/html,<script>","title":"XSS","snippet":"…"},
              {"url":"/relative/path","title":"Relative","snippet":"…"},
              {"url":"https://bad.example/a|https://bad.example/b","title":"Pipe","snippet":"…"},
              {"url":"https://bad.example/c\nInjected line","title":"Newline","snippet":"…"},
              {"url":"","title":"Empty","snippet":"…"},
              {"title":"No url at all","snippet":"…"},
              {"url":42}
            ]
            """;

        var urls = AgentOrchestrator.ExtractWebReferences(observation);

        Assert.Equal(new[] { "https://ok.example/a", "http://ok.example/b" }, urls);
    }

    /// <summary>
    /// search_web answers failures with bracketed prose, not JSON ("[Web search is
    /// temporarily unavailable.]", "[Tìm kiếm web đang tắt cho phiên này]", the
    /// whitelist refusal). Both shapes start with '[', so the extractor must decide on
    /// a real parse and fall back to "no citations" rather than throwing mid-stream.
    /// </summary>
    [Theory]
    [InlineData("[Web search is temporarily unavailable.]")]
    [InlineData("[Tìm kiếm web đang tắt cho phiên này]")]
    [InlineData("[Công cụ này không khả dụng cho agent hiện tại]")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"url\":\"https://example.com\"}")]
    [InlineData("not json at all")]
    public void ExtractWebReferences_YieldsNothing_ForEveryNonHitObservation(string observation)
    {
        Assert.Empty(AgentOrchestrator.ExtractWebReferences(observation));
    }

    /// <summary>
    /// The producer contract end to end: the real serialization a successful
    /// <see cref="LmKitOmniApi.Application.Abstractions.WebSearchOutcome"/> hands the
    /// ReAct loop goes in, and a marker the shipping stripper removes cleanly — with
    /// the answer intact — comes out.
    /// </summary>
    [Fact]
    public void RealWebSearchOutcome_BecomesAMarkerTheStripperRemovesWithoutEatingTheAnswer()
    {
        var resultsJson = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { url = "https://vnexpress.net/bai-viet", title = "Nguồn A", snippet = "…" },
            new { url = "https://tuoitre.vn/bai-viet", title = "Nguồn B", snippet = "…" },
        });
        var toolOutput = LmKitOmniApi.Application.Abstractions.WebSearchOutcome
            .Success(resultsJson)
            .ToToolOutput();

        var urls = AgentOrchestrator.ExtractWebReferences(toolOutput);
        Assert.Equal(new[] { "https://vnexpress.net/bai-viet", "https://tuoitre.vn/bai-viet" }, urls);

        var stripped = StripProtocolMarkers(
            AgentOrchestrator.FormatWebSearchMarker(urls) + OneParagraphAnswer);

        Assert.Equal(OneParagraphAnswer, stripped);
    }

    // ── 4. A real producer: ComputerUseAgent's emitted markers must hold the contract ──

    [Fact]
    public async Task RealAgentStream_EmitsRealNewlines_AndTheAnswerSurvivesStripping()
    {
        var output = await RunHermeticComputerUseAgentAsync();

        var thinking = output.Where(chunk => chunk.StartsWith("[THINKING]:", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(thinking);

        foreach (var marker in thinking)
        {
            Assert.DoesNotContain(@"\n", marker, StringComparison.Ordinal);
            Assert.EndsWith("\n", marker, StringComparison.Ordinal);
        }

        // End to end: the real emitted stream, followed by a one-paragraph answer, still
        // yields that answer after the server stripper runs.
        var persisted = string.Concat(output) + OneParagraphAnswer;
        var stripped = StripProtocolMarkers(persisted);

        Assert.Contains(OneParagraphAnswer, stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("[THINKING]", stripped, StringComparison.Ordinal);
    }

    // ── Hermetic ComputerUseAgent harness (fake executor / scripted model + approver) ──

    private static async Task<List<string>> RunHermeticComputerUseAgentAsync()
    {
        var executor = new StubExecutor();
        var model = new ScriptedModel(new[]
        {
            "{\"action\":\"screenshot\"}",
            "{\"action\":\"done\",\"summary\":\"đã xong nhiệm vụ\"}",
        });

        var options = new ComputerUseOptions
        {
            Enabled = true,
            Image = "computer-use/browser:latest",
            MaxSteps = 5,
            StepTimeoutSeconds = 30,
            SessionWallClockSeconds = 300,
            RequireApprovalPerAction = true,
            // A literal public IP short-circuits DNS in the landing re-validation, keeping
            // the run hermetic (same trick as ComputerUseAgentTests).
            AllowedHosts = new List<string> { "1.1.1.1" },
        };

        var sandbox = new ToolSandboxService(NullLogger<ToolSandboxService>.Instance);
        var agent = new ComputerUseAgent(
            executor,
            model,
            new AlwaysApprove(),
            Options.Create(options),
            new UserResourceAccessService(sandbox),
            sandbox,
            NullLogger<ComputerUseAgent>.Instance,
            audit: null);

        var request = new ComputerUseRequest(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "User", "quan sát trang rồi dừng", "");

        var collected = new List<string>();
        await foreach (var chunk in agent.RunAsync(request, CancellationToken.None))
            collected.Add(chunk);
        return collected;
    }

    private sealed class StubExecutor : IComputerUseExecutor
    {
        public bool IsEnabled => true;

        public Task<ComputerUseObservation> StepAsync(
            ComputerUseAction action, Guid tenantId, Guid userId, string sessionDirectory, CancellationToken ct)
            => Task.FromResult(new ComputerUseObservation { Url = "https://1.1.1.1/", Title = "trang thử" });
    }

    private sealed class ScriptedModel : IComputerUseModel
    {
        private readonly Queue<string> _responses;
        public ScriptedModel(IEnumerable<string> responses) => _responses = new Queue<string>(responses);

        public Task<string> DecideNextActionAsync(ComputerUsePrompt prompt, CancellationToken ct)
            => Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : "{\"action\":\"done\",\"summary\":\"done\"}");
    }

    private sealed class AlwaysApprove : IComputerUseApprovalGate
    {
        public Task<bool> RequestAsync(ComputerUseApprovalRequest request, CancellationToken ct)
            => Task.FromResult(true);
    }
}
