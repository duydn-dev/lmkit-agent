using System.Text.RegularExpressions;
using LmKitOmniApi.Application.Abstractions;

namespace LmKitOmniApi.Infrastructure.AI;

/// <summary>
/// Mức 3 — "agent tự suy filter": derives a <see cref="RetrievalMetadataFilter"/>
/// from the user's own words so a chat question like "tìm trong tài liệu PDF về X"
/// or "tài liệu tải lên năm 2025" narrows retrieval by metadata automatically.
///
/// It is deliberately CONSERVATIVE and deterministic (no extra model call): it fires
/// only on explicit signals, so it never silently shrinks recall for an ordinary
/// question. A bare year in the content ("doanh thu 2025") is NOT treated as an
/// upload-date filter — a date range is extracted only alongside an explicit
/// upload-time phrase. Returns <c>null</c> when nothing confident is found.
/// </summary>
public static class RagQueryFilterExtractor
{
    // Format tokens unambiguous enough to stand on their own.
    private static readonly (string Cue, string DocType)[] StrongTypeCues =
    [
        ("pdf", "pdf"),
        ("excel", "excel"), ("xlsx", "excel"),
        ("powerpoint", "powerpoint"), ("pptx", "powerpoint"),
        ("docx", "word"),
    ];

    // Ambiguous words — only honored when the query also mentions a file/document.
    private static readonly (string Cue, string DocType)[] WeakTypeCues =
    [
        ("word", "word"),
        ("bảng tính", "excel"), ("spreadsheet", "excel"),
        ("slide", "powerpoint"), ("trình chiếu", "powerpoint"),
        ("hình ảnh", "image"), ("bức ảnh", "image"), ("tấm ảnh", "image"), ("image", "image"),
        ("markdown", "markdown"),
    ];

    private static readonly string[] FileContextCues =
        ["file", "tệp", "tập tin", "tài liệu", "định dạng", "document", "đính kèm", "văn bản"];

    private static readonly string[] UploadTimeCues =
        ["tải lên", "upload", "đăng tải", "đưa lên"];

    private static readonly Regex YearPattern = new(@"\b(20\d{2})\b", RegexOptions.Compiled);

    public static RetrievalMetadataFilter? FromQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var q = query.ToLowerInvariant();

        var docTypes = ExtractDocTypes(q);
        var (afterUnix, beforeUnix) = ExtractUploadRange(q);

        if ((docTypes is null || docTypes.Count == 0) && afterUnix is null && beforeUnix is null)
            return null;

        return new RetrievalMetadataFilter
        {
            DocTypes = docTypes,
            UploadedAfterUnix = afterUnix,
            UploadedBeforeUnix = beforeUnix
        };
    }

    private static List<string>? ExtractDocTypes(string q)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (cue, docType) in StrongTypeCues)
            if (q.Contains(cue, StringComparison.Ordinal)) types.Add(docType);

        var hasFileContext = FileContextCues.Any(c => q.Contains(c, StringComparison.Ordinal));
        if (hasFileContext)
            foreach (var (cue, docType) in WeakTypeCues)
                if (q.Contains(cue, StringComparison.Ordinal)) types.Add(docType);

        return types.Count == 0 ? null : types.ToList();
    }

    private static (long? After, long? Before) ExtractUploadRange(string q)
    {
        // Only interpret a year as an UPLOAD-date filter when the user explicitly
        // talks about uploading/adding — otherwise a year is just part of the question.
        if (!UploadTimeCues.Any(c => q.Contains(c, StringComparison.Ordinal)))
            return (null, null);

        var years = YearPattern.Matches(q)
            .Select(m => int.Parse(m.Value))
            .Where(y => y is >= 2000 and <= 2100)
            .Distinct()
            .OrderBy(y => y)
            .ToList();
        if (years.Count == 0) return (null, null);

        var after = new DateTimeOffset(new DateTime(years.First(), 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var before = new DateTimeOffset(new DateTime(years.Last() + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return (after, before);
    }
}
