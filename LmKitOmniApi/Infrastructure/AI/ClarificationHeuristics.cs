using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LmKitOmniApi.Infrastructure.AI;

/// <summary>
/// Safety net cho clarification: model nhỏ (gemma-4-e4b) thường hiểu ĐÚNG là phải hỏi lại
/// người dùng nhưng KHÔNG gọi được tool <c>ask_clarification</c> — nó viết luôn câu hỏi và
/// các lựa chọn vào "Final Answer" dưới dạng danh sách văn xuôi. Người dùng chỉ thấy một
/// đoạn text, không bấm được lựa chọn nào, trong khi ChatGPT/Claude mở các option cho bấm.
///
/// Lớp này nhận diện câu trả lời "thuần hỏi lại" và dựng lại thành
/// <see cref="ClarificationRequest"/> để pipeline phát <c>[CLARIFICATION:...]</c> y như khi
/// tool được gọi thật.
///
/// Cố tình BẢO THỦ: chỉ nhận khi TOÀN BỘ nội dung là 2-3 mục danh sách, MỌI mục đều là câu
/// hỏi (kết thúc bằng '?'), phần dẫn và phần kết đều rất ngắn và không chứa câu hỏi nào
/// khác, và không có dấu hiệu của một câu trả lời thật (code block, bảng, link). Một câu
/// trả lời thật dài kèm vài câu hỏi phụ vì thế không bao giờ bị biến thành card.
/// </summary>
internal static class ClarificationHeuristics
{
    /// <summary>Câu trả lời dài hơn mức này gần như chắc chắn đã có nội dung thật → không đụng vào.</summary>
    private const int MaxAnswerChars = 1500;

    /// <summary>Một lời hỏi lại thuần văn xuôi phải rất ngắn, nếu không thì đã có nội dung thật.</summary>
    private const int MaxProseAskChars = 500;

    /// <summary>
    /// Một mục của danh sách câu hỏi phải ngắn. Mục dài là dấu hiệu mục đó đã chứa câu trả lời
    /// ("Docker là gì? Docker là một nền tảng ...") chứ không phải một lựa chọn để bấm.
    /// </summary>
    private const int MaxListItemChars = 220;

    /// <summary>
    /// Phần văn xuôi bao quanh danh sách (dẫn + kết) phải ngắn thì câu trả lời mới thực sự LÀ một
    /// lời hỏi lại thay vì một câu trả lời có kèm danh sách câu hỏi phụ.
    /// </summary>
    private const int MaxSurroundingChars = 500;

    /// <summary>Phần dẫn (trước mục đầu tiên) phải ngắn, nếu không thì đó là câu trả lời có kèm hỏi.</summary>
    private const int MaxLeadChars = 400;

    /// <summary>Nhãn option bị giới hạn 100 ký tự bởi <see cref="ClarificationRequest.Validate"/>.</summary>
    private const int MaxLabelChars = 100;

    /// <summary>Giá trị option bị giới hạn 500 ký tự bởi <see cref="ClarificationRequest.Validate"/>.</summary>
    private const int MaxValueChars = 500;

    private const string FallbackQuestion = "Bạn muốn chọn phương án nào?";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Một mục danh sách: "1." / "2)" / "- " / "* " / "• ".</summary>
    private static readonly Regex ListItemPattern = new(
        @"^\s*(?:\d{1,2}[\.\)]|[-*\u2022+]\s)\s*(?<text>.*\S)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Danh sách viết liền trong một dòng ("... 1. A? 2. B? 3. C?") — model hay trả về kiểu này.
    /// Chỉ tách trước số có dấu '.'/')' theo sau là khoảng trắng, nên "1.5" hay "2026." an toàn.
    ///
    /// ĐIỂM TÁCH ĐỨNG TRƯỚC SỐ không chỉ là khoảng trắng: đo trên model THẬT, model viết liền sau
    /// dấu câu — "... Ví dụ:1. **Chủ đề:** ...?2. **Thời gian:** ...?3. **Loại tài liệu:**" — nên bộ
    /// tách chỉ-chấp-nhận-khoảng-trắng bỏ sót toàn bộ danh sách và người dùng nhận một câu hỏi văn
    /// xuôi không bấm được (đúng lỗi gốc). Cho phép cả dấu câu làm ranh giới (lookbehind giữ nguyên
    /// ký tự đứng trước).
    /// </summary>
    private static readonly Regex InlineNumberPattern = new(
        @"(?<=[\s:;\.\]\)!?])(?=\d{1,2}[\.\)]\s)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Tách câu để lấy câu dẫn cuối cùng làm tiêu đề của card.</summary>
    private static readonly Regex SentenceSplitPattern = new(
        @"(?<=[\.\!\?\:\u2026])\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Tách câu theo dấu KẾT CÂU, khác <see cref="SentenceSplitPattern"/> ở chỗ KHÔNG tách tại
    /// dấu ':'. Dấu ':' thường nằm giữa câu — "định dạng báo cáo là gì (ví dụ: văn bản Word...)?" —
    /// và nếu tách ở đó thì mảnh cuối không còn kết thúc bằng '?' nên một lời hỏi lại thật bị bỏ sót.
    /// </summary>
    private static readonly Regex SentenceEndSplitPattern = new(
        @"(?<=[\.\!\?\u2026])\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Dựng <see cref="ClarificationRequest"/> từ câu trả lời văn xuôi, hoặc trả về
    /// <c>null</c> khi câu trả lời không phải là một lời hỏi lại thuần tuý.
    /// </summary>
    public static ClarificationRequest? TryBuild(string? answer) => TryBuild(answer, out _);

    /// <summary>
    /// Như <see cref="TryBuild(string?)"/>, nhưng trả thêm phần văn xuôi KHÔNG thuộc card
    /// (<paramref name="remainingText"/>) để caller phát nốt nó trước card. Chuyển một câu trả lời
    /// thành card mà nuốt luôn đoạn văn quanh nó là mất nội dung thật (một con số, một kết luận) —
    /// nên câu dẫn đã thành tiêu đề card bị cắt ra, phần còn lại vẫn phải hiện cho người dùng đọc.
    /// </summary>
    public static ClarificationRequest? TryBuild(string? answer, out string remainingText)
    {
        remainingText = string.Empty;

        var text = (answer ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > MaxAnswerChars) return null;
        if (HasAnswerMarkers(text)) return null;

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        // Nhánh 2: model viết ĐÚNG định dạng mà công cụ ask_clarification mô tả trong prompt
        // ("2-3 lựa chọn PHÂN TÁCH BẰNG ';', lựa chọn đề xuất có '*' ở đầu") nhưng lại đưa vào
        // CÂU TRẢ LỜI thay vì tham số công cụ — đo được trên gemma-4:
        // "...Vui lòng chọn...tốt nhất.*Tạo báo cáo mới...; Báo cáo tóm tắt...; Hướng dẫn quy trình...".
        // Danh sách kiểu này không có số thứ tự/gạch đầu dòng nên ExtractListItems bỏ sót hoàn toàn.
        var semicolonCard = TryBuildSemicolonChoices(normalized, out remainingText);
        if (semicolonCard is not null) return semicolonCard;

        var items = ExtractListItems(normalized, out var lead, out var trail);
        if (items.Count is < 2 or > 3) return null;

        // Điểm tựa của heuristic: MỌI mục đều là câu hỏi. Một danh sách lựa chọn trần
        // ("A / B / C") không đủ tin cậy để biến thành card, còn toàn câu hỏi thì gần
        // như chắc chắn model đang muốn hỏi lại. Mục bị giới hạn độ dài vì một mục dài là
        // câu hỏi kèm luôn câu trả lời (FAQ), không phải một lựa chọn để bấm.
        // Đo trên model THẬT (2026-10-02): chỉ mục đầu kết thúc bằng '?', các mục sau chấm bằng dấu
        // chấm ("**Bạn cần tóm tắt...:** Nếu vậy, bạn vui lòng đính kèm file PDF, Word... để tôi
        // hỗ trợ."). Bắt "mọi mục" khiến cả lời hỏi lại thật rơi vào đường văn xuôi và người dùng
        // không có nút lựa chọn nào để bấm. Nay chấp nhận khi ĐA SỐ mục là câu hỏi, HOẶC phần dẫn
        // có dấu hiệu xin thêm thông tin — vẫn loại được danh sách bước của một câu trả lời thật.
        var questionItems = items.Count(item => item.Contains('?'));
        var leadAsksForInfo = ClarificationCues.Any(cue => lead.Contains(cue, StringComparison.OrdinalIgnoreCase));
        if (questionItems < 2 && !leadAsksForInfo) return null;
        if (items.Any(item => item.Length is < 3 or > MaxListItemChars)) return null;

        // Văn xuôi hai đầu phải ngắn: câu trả lời thật thường dài ở đây, và ở đó phải im lặng.
        if (lead.Length + trail.Length > MaxSurroundingChars) return null;

        var options = BuildOptions(items, ToCardOption);
        var question = DeriveQuestion(lead);
        remainingText = BuildRemainingText(lead, trail);
        return ClarificationRequest.Validate(question, JsonSerializer.Serialize(options, JsonOptions));
    }

    /// <summary>
    /// Phần văn xuôi còn lại sau khi câu cuối của đoạn dẫn đã trở thành tiêu đề card: các câu
    /// trước đó của đoạn dẫn, cộng câu kết. Bỏ câu cuối để nội dung không bị đọc hai lần
    /// (một lần trong thân tin nhắn, một lần trên tiêu đề card).
    /// </summary>
    private static string BuildRemainingText(string lead, string trail)
    {
        var sentences = SentenceSplitPattern
            .Split(lead.Replace('\n', ' '))
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToList();
        var leadPrefix = sentences.Count > 1 ? string.Join(" ", sentences.Take(sentences.Count - 1)) : string.Empty;

        return string.Join("\n\n", new[] { leadPrefix, trail }.Where(part => part.Length > 0));
    }

    /// <summary>
    /// Lưới an toàn THỨ HAI: model viết lời hỏi lại thành một đoạn văn toàn câu hỏi
    /// ("... bạn vui lòng cho tôi biết ...? Bạn muốn báo cáo về chủ đề gì, và định dạng nào
    /// (Word, Excel, PDF)?") nên không có mục danh sách nào để dựng lựa chọn. Ở dạng đó card
    /// phải do một lượt định dạng lại sinh ra — xem ParseReformatted. Hàm này chỉ TRẢ LỜI CÂU
    /// HỎI "có đáng gọi thêm lượt đó không", và bảo thủ theo hướng ngược lại: chỉ khi TOÀN BỘ
    /// câu trả lời là câu hỏi (không có câu trần thuật nào), tối đa 3 câu, và rất ngắn.
    /// </summary>
    public static bool LooksLikeProseClarificationAsk(string? answer)
    {
        var text = (answer ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > MaxProseAskChars) return false;
        if (HasAnswerMarkers(text)) return false;

        var sentences = SentenceEndSplitPattern
            .Split(text.Replace('\n', ' ').Replace("\r", string.Empty, StringComparison.Ordinal))
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToList();

        if (sentences.Count is < 1 or > 3) return false;
        // Mọi câu đều phải là câu hỏi và phải đủ dài để là một câu hỏi thật.
        return sentences.All(sentence => sentence.EndsWith('?') && sentence.Length >= 8);
    }

    /// <summary>
    /// Đọc kết quả của lượt định dạng lại thành card. Chịu được cả khi model bọc JSON trong
    /// code fence hay kèm lời dẫn: chỉ cắt từ '{' đầu tiên tới '}' cuối cùng.
    /// </summary>
    public static ClarificationRequest? ParseReformatted(string? completion)
    {
        if (string.IsNullOrWhiteSpace(completion)) return null;

        var start = completion.IndexOf('{');
        var end = completion.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try
        {
            var dto = JsonSerializer.Deserialize<ReformattedClarification>(
                completion[start..(end + 1)], JsonOptions);
            if (dto is null || string.IsNullOrWhiteSpace(dto.Question) || dto.Choices is null) return null;

            var options = BuildOptions(dto.Choices, ToCardOption);
            return ClarificationRequest.Validate(
                dto.Question.Trim(), JsonSerializer.Serialize(options, JsonOptions));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Các câu dẫn danh sách rỗng nghĩa ("Ví dụ:", "Cụ thể:"...). Chỉ dùng để KHÔNG lấy làm tiêu
    /// đề card — so khớp CHÍNH XÁC, không phải contains, nên câu "Cụ thể, bạn cần báo cáo về" vẫn giữ.
    /// </summary>
    /// <summary>
    /// Dấu hiệu ngôn ngữ của một lời hỏi lại. Chỉ là ĐIỀU KIỆN phụ cho đường dựng card khi model
    /// quên dấu chấm hỏi — danh sách bước của một câu trả lời thật không chứa các cụm này.
    /// </summary>
    private static readonly string[] ClarificationCues =
        ["vui lòng", "cho tôi biết", "cần thêm", "cung cấp thêm", "bạn muốn", "bạn cần", "xin cho",
         "nêu rõ", "rõ hơn", "cụ thể hơn", "chi tiết hơn", "chủ đề nào", "loại nào", "dạng nào",
         "bạn đang", "bạn định"];

    /// <summary>
    /// Vị trí dấu kết câu mở đầu một câu MỚI được viết dính liền (không khoảng trắng): trả về
    /// index của dấu đó, hoặc -1. Cần dấu kết câu + CHỮ HOA ngay sau nên "e.g. Báo cáo" hay
    /// "(Ví dụ: ...)" không bị cắt nhầm — chỉ dạng ")?Bạn" / ").Bạn" mới là câu mới.
    /// </summary>
    private static int GluedSentenceStart(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] is not ('.' or '!' or '?')) continue;
            if (char.IsWhiteSpace(text[i + 1])) continue;
            if (char.IsUpper(text[i + 1])) return i;
        }

        return -1;
    }

    private static readonly HashSet<string> ListLeadInPhrases =
        new(StringComparer.OrdinalIgnoreCase) { "ví dụ", "cụ thể", "bao gồm", "như sau", "gồm", "chi tiết", "các lựa chọn", "lựa chọn" };

    /// <summary>
    /// Dựng card từ định dạng lựa chọn PHÂN TÁCH BẰNG ';' với lựa chọn đề xuất đánh dấu '*' — đúng
    /// định dạng công cụ <c>ask_clarification</c> mô tả, mà model hay viết thẳng vào câu trả lời.
    ///
    /// CỐ TÌNH HẸP: cần đúng MỘT dấu '*' nằm TRƯỚC dấu ';' đầu tiên (đề xuất thường đứng đầu) và
    /// KHÔNG phải một phần của <c>**</c> markdown; 2-3 phần, mỗi phần ngắn. Nhờ vậy câu trả lời thật
    /// có dấu ';' và chữ in đậm không bị biến thành card.
    /// </summary>
    private static ClarificationRequest? TryBuildSemicolonChoices(string text, out string remainingText)
    {
        remainingText = string.Empty;

        var star = text.IndexOf('*');
        if (star <= 0) return null;
        // Loại trường hợp markdown "**...**" — dấu '*' phải đứng một mình.
        if (text[star - 1] == '*' || (star + 1 < text.Length && text[star + 1] == '*')) return null;

        var firstSemicolon = text.IndexOf(';');
        if (firstSemicolon < 0 || star > firstSemicolon) return null;

        var lead = text[..star].Trim();
        if (lead.Length is < 4 || lead.Length > MaxLeadChars) return null;

        var parts = text[star..]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0)
            .ToList();
        if (parts.Count is < 2 or > 3) return null;

        var choices = parts
            .Select(part => part.TrimStart('*').Trim())
            .ToList();
        if (choices.Any(choice => choice.Length is < 1 or > MaxListItemChars)) return null;

        // Phần tử đầu là lựa chọn ĐỀ XUẤT (đó là lý do dấu '*' đứng trước ';' đầu tiên).
        choices[0] = "*" + choices[0];

        var options = BuildOptions(choices, ToCardOption);
        var question = DeriveQuestion(lead);
        remainingText = BuildRemainingText(lead, trail: string.Empty);
        return ClarificationRequest.Validate(question, JsonSerializer.Serialize(options, JsonOptions));
    }

    /// <summary>
    /// Chuẩn hoá lựa chọn thô thành đúng 2-3 <see cref="ClarificationOption"/> với ĐÚNG MỘT
    /// lựa chọn được đề xuất: dấu '*' ở đầu một phần tử đánh dấu lựa chọn đề xuất, không đánh
    /// dấu thì lấy phần tử đầu, đánh dấu nhiều thì giữ cái đầu tiên. Đây là quy tắc mà cả
    /// <see cref="Tools.ClarificationTool"/> lẫn đường dựng lại từ văn xuôi phải tuân theo.
    /// </summary>
    internal static List<ClarificationOption> BuildOptions(
        IReadOnlyList<string> rawChoices, Func<string, (string Label, string Value)> toOption)
    {
        var parsed = rawChoices
            .Select(raw => (raw ?? string.Empty).Trim())
            .Where(trimmed => trimmed.Length > 0)
            .Take(3)
            .Select(trimmed => trimmed.StartsWith('*')
                ? (Text: trimmed[1..].Trim(), Recommended: true)
                : (Text: trimmed, Recommended: false))
            .ToList();

        if (parsed.Count < 2)
        {
            throw new ArgumentException(
                "Provide 2 or 3 choices; mark the recommended one with a leading '*'.", nameof(rawChoices));
        }

        var firstRecommended = parsed.FindIndex(option => option.Recommended);
        if (firstRecommended < 0) firstRecommended = 0;

        return parsed
            .Select((option, index) =>
            {
                var (label, value) = toOption(option.Text);
                return new ClarificationOption(label, value, index == firstRecommended);
            })
            .ToList();
    }

    /// <summary>Nhãn là lựa chọn đã bỏ dấu hỏi (nút bấm gọn); value giữ nguyên câu hỏi gốc.</summary>
    private static (string Label, string Value) ToCardOption(string raw)
    {
        var cleaned = StripMarkdown(raw);
        return (Truncate(cleaned.TrimEnd('?', ' ').Trim(), MaxLabelChars), Truncate(cleaned, MaxValueChars));
    }

    /// <summary>Dấu hiệu của một câu trả lời thật: có nội dung để đọc/bấm, không phải lời hỏi lại.</summary>
    private static bool HasAnswerMarkers(string text) =>
        text.Contains("```", StringComparison.Ordinal)
        || text.Contains("http://", StringComparison.OrdinalIgnoreCase)
        || text.Contains("https://", StringComparison.OrdinalIgnoreCase)
        || text.Contains('|')
        // Đã là marker có cấu trúc rồi thì không xử lý lại (tránh đệ quy/đúp card).
        || text.Contains("[CLARIFICATION", StringComparison.Ordinal);

    /// <summary>
    /// Lấy các mục danh sách; phần trước mục đầu là <paramref name="lead"/>, phần sau mục cuối
    /// là <paramref name="trail"/>. Mục nhiều dòng được nối lại (model hay xuống dòng giữa câu).
    /// </summary>
    private static List<string> ExtractListItems(string text, out string lead, out string trail)
    {
        var lines = text.Split('\n');
        var inlineList = !lines.Any(line => ListItemPattern.IsMatch(line));
        if (inlineList)
        {
            // Không có mục nào xuống dòng → thử tách danh sách viết liền trên một dòng.
            lines = InlineNumberPattern.Replace(text, "\n").Split('\n');
        }

        var items = new List<StringBuilder>();
        var leadLines = new List<string>();
        var trailLines = new List<string>();
        var listClosed = false;

        foreach (var line in lines)
        {
            var match = ListItemPattern.Match(line);
            if (match.Success)
            {
                listClosed = false;
                items.Add(new StringBuilder(match.Groups["text"].Value.Trim()));
                continue;
            }

            if (items.Count == 0)
            {
                leadLines.Add(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                // Dòng trống đóng danh sách: mọi thứ sau nó là phần kết.
                listClosed = true;
                continue;
            }

            if (listClosed)
            {
                trailLines.Add(line);
                continue;
            }

            // Xuống dòng giữa một mục (wrap) → nối vào mục đang mở.
            var current = items[^1];
            if (current.Length > 0) current.Append(' ');
            current.Append(line.Trim());
        }

        if (inlineList)
        {
            // Danh sách viết liền: câu văn nối sau dấu '?' cuối cùng của mục cuối KHÔNG phải một
            // lựa chọn (đo thực tế: "...có sẵn?Nếu bạn đang tìm kiếm thông tin chung...giúp bạn!").
            // Dính vào mục thì mục vượt MaxListItemChars và cả card bị bỏ — trong khi đúng phần đó
            // lại là văn xuôi người dùng cần đọc. Cắt tại '?' cuối và trả về phần kết.
            for (var i = 0; i < items.Count; i++)
            {
                var value = items[i].ToString();
                var splitAt = GluedSentenceStart(value);
                if (splitAt < 0) continue;

                var tail = value[(splitAt + 1)..].Trim();
                if (tail.Length > 0) trailLines.Insert(0, tail);
                items[i] = new StringBuilder(value[..(splitAt + 1)]);
            }
        }

        lead = string.Join("\n", leadLines).Trim();
        trail = string.Join("\n", trailLines).Trim();
        return items.Select(builder => builder.ToString().Trim()).ToList();
    }

    /// <summary>
    /// Tiêu đề card: câu cuối của phần dẫn ("Bạn vui lòng cho tôi biết:" → "Bạn vui lòng cho tôi biết").
    /// Không suy ra được thì dùng câu hỏi trung tính.
    /// </summary>
    private static string DeriveQuestion(string lead)
    {
        var flat = string.Join(" ",
            lead.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (flat.Length == 0) return FallbackQuestion;

        var sentences = SentenceSplitPattern.Split(flat)
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToList();
        if (sentences.Count == 0) return FallbackQuestion;

        // Câu cuối của đoạn dẫn có thể chỉ là câu dẫn danh sách vô nghĩa ("Ví dụ:") — tiêu đề card
        // "Ví dụ" không nói gì cho người dùng. Lùi về câu trước đó trong trường hợp này.
        var candidate = string.Empty;
        for (var i = sentences.Count - 1; i >= 0; i--)
        {
            var text = StripMarkdown(sentences[i]).TrimEnd(':', ' ').Trim();
            if (text.Length == 0) continue;
            if (i > 0 && ListLeadInPhrases.Contains(text.ToLowerInvariant())) continue;
            candidate = text;
            break;
        }

        return candidate.Length is < 4 or > MaxLeadChars ? FallbackQuestion : candidate;
    }

    private static string StripMarkdown(string value) =>
        value.Replace("**", string.Empty, StringComparison.Ordinal)
             .Replace("__", string.Empty, StringComparison.Ordinal)
             .Replace("*", string.Empty, StringComparison.Ordinal)
             .Replace("`", string.Empty, StringComparison.Ordinal)
             .Trim();

    private static string Truncate(string value, int max)
    {
        var collapsed = Regex.Replace(value, @"\s+", " ").Trim();
        return collapsed.Length <= max ? collapsed : collapsed[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>Hợp đồng JSON của lượt định dạng lại (xem AgentOrchestrator).</summary>
    private sealed record ReformattedClarification(string? Question, List<string>? Choices);
}
