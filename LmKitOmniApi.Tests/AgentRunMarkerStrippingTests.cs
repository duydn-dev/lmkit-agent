using LmKitOmniApi.Application.AgentRuns;
using Xunit;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pins <see cref="AgentRunMarkers.StripMarkers"/> against the shape the orchestrator actually
/// emits, which is where it had been failing.
///
/// <para>The bug: the terminator alternation could not match a NEWLINE-terminated marker, so
/// <c>[STEP:{…}]</c> and <c>[FILE:{…}]</c> were stripped — they close with <c>]</c> — while every
/// <c>[THINKING]:</c> and <c>[REASONING]:</c> line survived into the stored
/// <c>AgentRun.Result</c>. It failed safely, leaving noise rather than eating the answer, which
/// is exactly why it went unnoticed: nothing was ever visibly wrong enough to investigate.</para>
///
/// <para>The prose-survives assertions matter as much as the stripping ones. A marker regex that
/// over-matches is the far worse failure, and this repo has been bitten twice by strippers eating
/// the answer itself.</para>
/// </summary>
public class AgentRunMarkerStrippingTests
{
    private const string Answer = "Doanh thu quý 4 tăng 12% so với quý trước.";

    /// <summary>
    /// The three LINE-shaped markers, in the exact form the orchestrator emits: <c>[NAME]:</c>
    /// then text to the end of the line. Every one of these survived into the stored result
    /// before the fix.
    /// </summary>
    [Theory]
    [InlineData("THINKING")]
    [InlineData("REASONING")]
    [InlineData("WEB_SEARCH")]
    public void NewlineTerminatedMarker_IsStripped_AndTheAnswerSurvives(string marker)
    {
        var raw = $"[{marker}]: đang xử lý…\n{Answer}";

        Assert.Equal(Answer, AgentRunMarkers.StripMarkers(raw));
    }

    /// <summary>
    /// The BRACKET-CLOSED markers, also in their real emitted form — <c>[AGENT_RUN:{id}]</c> and
    /// <c>[RESEARCH_SAVED:{rootId}]</c> are NOT line-shaped, whatever their all-caps names
    /// suggest. These already worked; they are pinned so fixing the line shape cannot break them.
    /// </summary>
    [Fact]
    public void BracketClosedMarkers_AreStillStripped()
    {
        var raw = $"[AGENT_RUN:{Guid.NewGuid()}]"
            + "[STEP:{\"action\":\"web_search\"}]"
            + "[FILE:{\"id\":\"abc\"}]"
            + $"[RESEARCH_SAVED:{Guid.NewGuid()}]"
            + Answer;

        Assert.Equal(Answer, AgentRunMarkers.StripMarkers(raw));
    }

    /// <summary>
    /// The second bug the split fixes: a single lazy pattern terminated on the first <c>[</c>
    /// inside the payload, and <c>[WEB_SEARCH]:</c> always carries a JSON ARRAY — so its payload
    /// leaked through as raw JSON while the marker itself disappeared.
    /// </summary>
    [Fact]
    public void AWebSearchPayloadIsJsonStartingWithABracket_AndDoesNotLeak()
    {
        var raw = "[WEB_SEARCH]: [{\"url\":\"https://example.com/a\",\"title\":\"Nguồn A\"}]\n" + Answer;

        var stripped = AgentRunMarkers.StripMarkers(raw);

        Assert.Equal(Answer, stripped);
        Assert.DoesNotContain("https://example.com", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void AFullStream_LeavesOnlyProse()
    {
        var raw = "[Agent invoked: Hermes]\n"
            + "[THINKING]: 🛡️ Kiểm tra bảo mật đầu vào...\n"
            + "[THINKING]: ✅ Đầu vào an toàn\n"
            + "[STEP:{\"ordinal\":1,\"action\":\"web_search\"}]"
            + "[WEB_SEARCH]: [{\"url\":\"https://example.com\",\"title\":\"A\"}]\n"
            + "[REASONING]: cân nhắc các nguồn\n"
            + Answer;

        var stripped = AgentRunMarkers.StripMarkers(raw);

        Assert.Equal(Answer, stripped);
        Assert.DoesNotContain("[", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void AnApprovalMarker_NeverSurvivesIntoAStoredResult()
    {
        // The consequence named in AgentRunMarkers' own remarks: a stray approval marker makes
        // the agent-run page adopt a gate for an approval that has already been answered.
        var raw = $"[HITL_APPROVAL_REQUIRED:{Guid.NewGuid()}]\n{Answer}";

        var stripped = AgentRunMarkers.StripMarkers(raw);

        Assert.Equal(Answer, stripped);
        Assert.DoesNotContain(AgentRunMarkers.ApprovalRequired, stripped, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Chi phí [đã bao gồm VAT] là 3 triệu.")]
    [InlineData("Mảng rỗng trong JSON viết là [].")]
    [InlineData("Xem mục [1] và [2] để biết chi tiết.")]
    [InlineData("Kết quả: [THINKING ABOUT IT] là tên một bài hát.")]
    public void SquareBracketsInOrdinaryProse_AreLeftAlone(string prose)
    {
        Assert.Equal(prose, AgentRunMarkers.StripMarkers(prose));
    }

    [Fact]
    public void AMarkerWithNoTrailingNewline_IsStripped()
    {
        Assert.Equal(string.Empty, AgentRunMarkers.StripMarkers("[THINKING]: đang xử lý…"));
    }
}
