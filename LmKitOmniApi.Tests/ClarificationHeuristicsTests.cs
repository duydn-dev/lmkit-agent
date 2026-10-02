using LmKitOmniApi.Infrastructure.AI;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Bảo vệ lưới an toàn clarification: model nhỏ không gọi được tool
/// <c>ask_clarification</c> nên viết câu hỏi + lựa chọn vào Final Answer dưới dạng văn xuôi,
/// và người dùng chỉ thấy text không bấm được.
/// </summary>
/// <remarks>
/// Ca "positive" đầu tiên là nguyên văn câu trả lời thật lấy từ DB (ChatMessages) sau khi
/// người dùng chọn "Báo cáo tổng hợp cả năm 2026": reasoning trace ghi rõ
/// "ask_clarification is the correct tool" rồi model vẫn trả lời bằng danh sách câu hỏi.
/// Các ca "negative" khoá lại phần bảo thủ: một câu trả lời thật, dù có kèm vài câu hỏi,
/// tuyệt đối không được biến thành card.
/// </remarks>
public sealed class ClarificationHeuristicsTests
{
    private const string LiveProseClarification = """
        Tôi đã sẵn sàng để tạo báo cáo tổng hợp cả năm 2026. Tuy nhiên, để tạo báo cáo, tôi cần nội dung hoặc dữ liệu. Bạn vui lòng cho tôi biết:

        1.  **Bạn muốn cung cấp dữ liệu trực tiếp** (ví dụ: dán nội dung, liệt kê số liệu)?
        2.  **Bạn có tệp dữ liệu nào** (Excel, Word...) muốn tôi đọc và tổng hợp không?
        3.  Hay bạn muốn tôi **tạo theo một mẫu chung** và bạn sẽ tự điền nội dung sau?

        Khi bạn cung cấp thông tin này, tôi sẽ tiến hành tạo báo cáo cho bạn.
        """;

    [Fact]
    public void LiveProseClarification_BecomesCard()
    {
        var request = ClarificationHeuristics.TryBuild(LiveProseClarification);

        Assert.NotNull(request);
        Assert.Equal("Bạn vui lòng cho tôi biết", request!.Question);
        Assert.Equal(3, request.Options.Count);
        // Đúng một lựa chọn được đề xuất — bất biến của ClarificationRequest.Validate.
        Assert.Equal(1, request.Options.Count(option => option.Recommended));
        Assert.True(request.Options[0].Recommended);
        // Markdown của model không được lọt vào nút bấm.
        Assert.DoesNotContain("**", request.Options[0].Label);
        Assert.EndsWith("liệt kê số liệu)?", request.Options[0].Value);
    }

    [Fact]
    public void InlineNumberedQuestions_BecomesCard()
    {
        var request = ClarificationHeuristics.TryBuild(
            "Bạn vui lòng cho tôi biết: 1. Xuất PDF? 2. Xuất Word?");

        Assert.NotNull(request);
        Assert.Equal(2, request!.Options.Count);
        Assert.Equal("Xuất PDF", request.Options[0].Label);
    }

    [Fact]
    public void BulletedQuestions_BecomesCard()
    {
        var request = ClarificationHeuristics.TryBuild("""
            Tôi cần thêm một thông tin:

            - Bạn muốn báo cáo theo tuần?
            - Bạn muốn báo cáo theo tháng?
            """);

        Assert.NotNull(request);
        Assert.Equal(2, request!.Options.Count);
        Assert.Equal("Bạn muốn báo cáo theo tuần", request.Options[0].Label);
    }

    [Fact]
    public void NoLeadOrTrail_FallsBackToNeutralQuestion()
    {
        var request = ClarificationHeuristics.TryBuild("""
            1. Xuất PDF?
            2. Xuất Word?
            """);

        Assert.NotNull(request);
        Assert.Equal("Bạn muốn chọn phương án nào?", request!.Question);
    }

    [Theory]
    // Danh sách mục không phải câu hỏi: một quy trình, không phải lời hỏi lại.
    [InlineData("1. Mở tệp. 2. Nhập dữ liệu. 3. Lưu lại.")]
    // Bốn lựa chọn: vượt hợp đồng của card (2-3).
    [InlineData("1. Xuất PDF?\n2. Xuất Word?\n3. Xuất Excel?\n4. Xuất PowerPoint?")]
    // Code block / link là dấu hiệu của câu trả lời thật.
    [InlineData("```json\n{}\n```\n1. Xuất PDF?\n2. Xuất Word?")]
    [InlineData("Xem https://example.com nhé.\n1. Xuất PDF?\n2. Xuất Word?")]
    // Đã có card rồi thì không dựng lại.
    [InlineData("[CLARIFICATION:{\"question\":\"A?\",\"options\":[]}]")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Không có gì để hỏi, đây là câu trả lời hoàn chỉnh.")]
    public void NonClarificationAnswers_AreLeftAlone(string answer) =>
        Assert.Null(ClarificationHeuristics.TryBuild(answer));

    /// <summary>Mục dài nghĩa là câu hỏi kèm luôn câu trả lời (kiểu FAQ), không phải lựa chọn để bấm.</summary>
    [Fact]
    public void LongFaqItems_AreNotChoices()
    {
        var explanation = new string('a', 260);
        Assert.Null(ClarificationHeuristics.TryBuild($"1. Docker là gì? {explanation}\n2. Kubernetes là gì? {explanation}"));
    }

    /// <summary>Văn xuôi hai đầu quá dài: đây là câu trả lời thật, không phải lời hỏi lại.</summary>
    [Fact]
    public void LongAnswerWithFollowUpQuestions_IsLeftAlone()
    {
        var paragraph = string.Join(" ", Enumerable.Repeat(
            "Doanh thu quý 3 đạt 12 tỷ đồng và biên lợi nhuận gộp cải thiện lên mức cao nhất trong ba quý gần đây.", 6));
        var answer = $"{paragraph}\n1. Xuất PDF?\n2. Xuất Word?";

        Assert.True(answer.Length > 500, "ca này phải vượt ngưỡng văn xuôi mới có ý nghĩa");
        Assert.Null(ClarificationHeuristics.TryBuild(answer));
    }

    [Fact]
    public void LongChoice_IsTruncatedInsideTheContract()
    {
        var longChoice = new string('x', 180);
        var request = ClarificationHeuristics.TryBuild($"Bạn chọn cách nào:\n1. {longChoice}?\n2. Ngắn gọn?");

        Assert.NotNull(request);
        Assert.True(request!.Options[0].Label.Length <= 100);
        Assert.True(request.Options[0].Value.Length <= 500);
    }

    /// <summary>
    /// Hình dạng thật thứ hai, đo được trên gemma-4-e4b: danh sách câu hỏi có ví dụ trong
    /// ngoặc nên mục KHÔNG kết thúc bằng '?'. Bản heuristic đầu tiên đòi kết thúc bằng '?'
    /// nên bỏ sót đúng ca này và người dùng lại nhận một đoạn văn.
    /// </summary>
    [Fact]
    public void LiveSubQuestionList_BecomesCard()
    {
        const string answer = """
            Để tôi có thể tạo báo cáo cho bạn, vui lòng cho tôi biết thêm chi tiết về yêu cầu của bạn được không ạ?

            Cụ thể, bạn cần báo cáo về:
            1.  **Chủ đề gì?** (Ví dụ: Báo cáo kinh doanh quý 3, Phân tích thị trường AI, v.v.)
            2.  **Dữ liệu nguồn là gì?** (Ví dụ: Bạn có file Excel/Word cần tôi tổng hợp không? Hay bạn muốn tôi tìm kiếm thông tin trên web?)
            3.  **Bạn muốn định dạng nào?** (Word (.docx) cho văn bản chính thức, PDF cho in ấn, hay Excel (.xlsx) cho số liệu?)

            Sau khi có các thông tin này, tôi sẽ tạo báo cáo theo đúng yêu cầu của bạn.
            """;

        var request = ClarificationHeuristics.TryBuild(answer, out var remainingText);

        Assert.NotNull(request);
        Assert.Equal(3, request!.Options.Count);
        Assert.Equal(1, request.Options.Count(option => option.Recommended));
        Assert.Equal("Cụ thể, bạn cần báo cáo về", request.Question);
        // Câu hỏi dẫn đã lên tiêu đề card; phần còn lại vẫn phải tới người dùng.
        Assert.Contains("vui lòng cho tôi biết", remainingText);
        Assert.Contains("tôi sẽ tạo báo cáo", remainingText);
    }

    /// <summary>
    /// Câu trả lời THẬT có kèm danh sách câu hỏi phụ: phải thành card nhưng TUYỆT ĐỐI không được
    /// nuốt mất nội dung — phần văn xuôi trả về chính là những gì người dùng còn cần đọc.
    /// </summary>
    [Fact]
    public void AnswerWithFollowUpQuestions_KeepsTheAnswerText()
    {
        const string answer = """
            Doanh thu quý 3 đạt 12 tỷ đồng, tăng 18% so với cùng kỳ. Biên lợi nhuận gộp cải thiện lên 34%.
            Bạn có muốn tôi xuất báo cáo này?
            1. Xuất PDF?
            2. Xuất Word?
            """;

        var request = ClarificationHeuristics.TryBuild(answer, out var remainingText);

        Assert.NotNull(request);
        Assert.Equal("Bạn có muốn tôi xuất báo cáo này?", request!.Question);
        Assert.Contains("12 tỷ đồng", remainingText);
        Assert.DoesNotContain("Xuất PDF", remainingText);
    }

    /// <summary>
    /// Nguyên văn câu trả lời của gemma-4-e4b cho "Tạo báo cáo": hỏi lại bằng MỘT đoạn văn
    /// toàn câu hỏi (không có danh sách), nên <see cref="ClarificationHeuristics.TryBuild"/> không
    /// dựng được lựa chọn nào — đây là ca phải đi qua lượt định dạng lại.
    /// </summary>
    private const string LiveProseQuestionParagraph =
        "Để tôi có thể tạo báo cáo, bạn vui lòng cho tôi biết thêm thông tin chi tiết được không ạ? "
        + "Bạn muốn báo cáo về chủ đề gì, và bạn muốn định dạng báo cáo là gì "
        + "(ví dụ: văn bản Word, bảng tính Excel, hay tài liệu PDF)?";

    [Fact]
    public void LiveProseQuestionParagraph_IsDetectedForReformatting()
    {
        Assert.True(ClarificationHeuristics.LooksLikeProseClarificationAsk(LiveProseQuestionParagraph));
        // Không có mục danh sách nào nên chưa dựng được card trực tiếp.
        Assert.Null(ClarificationHeuristics.TryBuild(LiveProseQuestionParagraph));
    }

    [Theory]
    // Có câu trần thuật → là câu trả lời thật kèm câu hỏi phụ.
    [InlineData("Hà Nội hôm nay không mưa. Bạn có cần tôi tra thêm gì không?")]
    [InlineData("Báo cáo đã xong. Bạn muốn tôi xuất PDF không?")]
    // Code block là dấu hiệu câu trả lời thật.
    [InlineData("```json\n{}\n```\nBạn muốn tôi chạy lại không?")]
    [InlineData("")]
    public void NonProseAsks_AreNotReformatted(string answer) =>
        Assert.False(ClarificationHeuristics.LooksLikeProseClarificationAsk(answer));

    [Fact]
    public void OverlongProseAsk_IsNotReformatted()
    {
        var longAsk = string.Join(" ",
            Enumerable.Repeat("Bạn muốn tôi xử lý phần dữ liệu này như thế nào?", 12));

        Assert.True(longAsk.Length > 500, "ca này phải vượt ngưỡng độ dài mới có ý nghĩa");
        Assert.False(ClarificationHeuristics.LooksLikeProseClarificationAsk(longAsk));
    }

    [Fact]
    public void ParseReformatted_ReadsJsonInsideNoise()
    {
        var request = ClarificationHeuristics.ParseReformatted(
            "```json\n{\"question\":\"Xuất báo cáo dạng nào?\",\"choices\":[\"*PDF\",\"Word\",\"Excel\"]}\n```");

        Assert.NotNull(request);
        Assert.Equal("Xuất báo cáo dạng nào?", request!.Question);
        Assert.Equal(3, request.Options.Count);
        Assert.True(request.Options[0].Recommended);
        Assert.Equal("PDF", request.Options[0].Label);
        Assert.DoesNotContain("*", request.Options[0].Label);
    }

    [Fact]
    public void ParseReformatted_WithoutMarker_RecommendsTheFirstChoice()
    {
        var request = ClarificationHeuristics.ParseReformatted(
            "{\"question\":\"Chọn định dạng?\",\"choices\":[\"Word\",\"Excel\"]}");

        Assert.NotNull(request);
        Assert.True(request!.Options[0].Recommended);
        Assert.False(request.Options[1].Recommended);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Xin chào, tôi không hiểu yêu cầu.")]
    [InlineData("{\"question\":\"Chỉ một lựa chọn?\",\"choices\":[\"Word\"]}")]
    [InlineData("{\"question\":\"\",\"choices\":[\"Word\",\"Excel\"]}")]
    [InlineData("{\"question\":\"Thiếu choices?\"}")]
    public void ParseReformatted_ReturnsNullWhenUnusable(string completion) =>
        Assert.Null(ClarificationHeuristics.ParseReformatted(completion));

    /// <summary>
    /// Tool và đường dựng lại từ văn xuôi dùng chung luật "đúng một recommended"; đổi luật ở
    /// một nơi mà quên nơi kia thì card hỏng ở đúng chỗ khó thấy nhất.
    /// </summary>
    [Fact]
    public async Task ClarificationTool_ChoicesBecomeOptionsWithOneRecommended()
    {
        string? optionsJson = null;
        var tool = new LmKitOmniApi.Infrastructure.AI.Tools.ClarificationTool(
            (_, json, _) =>
            {
                optionsJson = json;
                return Task.FromResult("ok");
            });

        await tool.InvokeAsync("""{"question":"Xuất dạng nào?","choices":"Word; *Excel; PDF"}""");

        var options = System.Text.Json.JsonSerializer.Deserialize<List<ClarificationOption>>(
            optionsJson!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotNull(options);
        Assert.Equal(3, options!.Count);
        Assert.Equal("Excel", options.Single(option => option.Recommended).Label);
    }

    /// <summary>
    /// Lời nhắc trong ReAct instruction là kênh duy nhất model đọc được (xem
    /// ReActPlannerInstructionTests). Nó phải nói thẳng rằng viết câu hỏi ra text là SAI,
    /// nếu không model lại "Final Answer" hoá lời hỏi lại như đã đo được trên gemma-4-e4b.
    /// </summary>
    [Fact]
    public void ReActInstruction_ForbidsProseClarification()
    {
        var instruction = AgentOrchestrator.BuildReActInstruction("tạo báo cáo tổng hợp", string.Empty, null);

        Assert.Contains("CLARIFICATION RULE", instruction);
        Assert.Contains("ask_clarification", instruction);
        Assert.Contains("INVALID answer", instruction);
        // Một lần duy nhất: bản trước bị lặp nguyên đoạn hai lần làm loãng chỉ dẫn.
        Assert.Equal(1, CountOccurrences(instruction, "CLARIFICATION RULE"));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
