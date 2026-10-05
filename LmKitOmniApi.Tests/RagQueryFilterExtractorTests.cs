using LmKitOmniApi.Infrastructure.AI;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Mức 3 — the agent's automatic query→metadata-filter extraction. Deterministic and
/// deliberately conservative: file-type filters fire on explicit format words; an
/// upload-date range fires ONLY alongside an explicit upload-time phrase, so a year
/// that is merely part of the content never silently shrinks recall.
/// </summary>
public sealed class RagQueryFilterExtractorTests
{
    private static long UnixJan1(int year) =>
        new DateTimeOffset(new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("chính sách bảo hành áp dụng bao lâu?")]   // no format / no upload cue
    [InlineData("doanh thu năm 2025 là bao nhiêu?")]       // bare year, NO upload cue → not a date filter
    public void PlainQuestions_YieldNoFilter(string query)
    {
        Assert.Null(RagQueryFilterExtractor.FromQuery(query));
    }

    [Fact]
    public void StrongFormatWords_ExtractDocType_WithoutNeedingAFileCue()
    {
        var pdf = RagQueryFilterExtractor.FromQuery("tổng hợp các báo cáo PDF về quan trắc");
        Assert.NotNull(pdf);
        Assert.Equal(new[] { "pdf" }, pdf!.DocTypes);

        var excel = RagQueryFilterExtractor.FromQuery("số liệu trong excel quý 3");
        Assert.Equal(new[] { "excel" }, excel!.DocTypes);
    }

    [Fact]
    public void WeakFormatWords_RequireAFileContextCue()
    {
        // "word" alone (no file/document cue) is ambiguous → ignored.
        Assert.Null(RagQueryFilterExtractor.FromQuery("định nghĩa của từ environment"));

        // With a file cue, it resolves to the Word doc type.
        var withCue = RagQueryFilterExtractor.FromQuery("mở tài liệu word về kế hoạch");
        Assert.NotNull(withCue);
        Assert.Equal(new[] { "word" }, withCue!.DocTypes);
    }

    [Fact]
    public void UploadYear_WithExplicitUploadPhrase_BecomesAOneYearRange()
    {
        var f = RagQueryFilterExtractor.FromQuery("tài liệu tải lên năm 2025 nói gì về nước thải?");
        Assert.NotNull(f);
        Assert.Equal(UnixJan1(2025), f!.UploadedAfterUnix);
        Assert.Equal(UnixJan1(2026), f.UploadedBeforeUnix);
    }

    [Fact]
    public void UploadRange_AcrossTwoYears_SpansMinToMaxPlusOne()
    {
        var f = RagQueryFilterExtractor.FromQuery("các tệp upload từ 2023 đến 2025");
        Assert.NotNull(f);
        Assert.Equal(UnixJan1(2023), f!.UploadedAfterUnix);
        Assert.Equal(UnixJan1(2026), f.UploadedBeforeUnix);
    }

    [Fact]
    public void FormatAndUploadDate_Combine()
    {
        var f = RagQueryFilterExtractor.FromQuery("tài liệu pdf tải lên năm 2024");
        Assert.NotNull(f);
        Assert.Equal(new[] { "pdf" }, f!.DocTypes);
        Assert.Equal(UnixJan1(2024), f.UploadedAfterUnix);
        Assert.Equal(UnixJan1(2025), f.UploadedBeforeUnix);
        Assert.False(f.IsEmpty);
    }
}
