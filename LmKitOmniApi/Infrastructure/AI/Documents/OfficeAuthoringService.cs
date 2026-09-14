using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Options;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace LmKitOmniApi.Infrastructure.AI.Documents;

/// <summary>
/// Cấu hình tool soạn file Office, bound từ "OfficeAuthoring". BẬT mặc định —
/// khác các tool egress: đây là soạn tài liệu THUẦN LOCAL (OpenXML, không model,
/// không mạng, không tiến trình ngoài), ghi vào đúng kho upload cô lập của chính
/// người dùng — cùng mức rủi ro với file run_python trả về, và đã có trần kích thước.
/// </summary>
public sealed class OfficeAuthoringOptions
{
    public const string SectionName = "OfficeAuthoring";

    public bool Enabled { get; set; } = true;

    /// <summary>Trần ký tự phần nội dung markdown của create_docx.</summary>
    public int MaxContentChars { get; set; } = 200_000;

    /// <summary>Trần tổng số dòng dữ liệu của một workbook create_xlsx.</summary>
    public int MaxRowsPerWorkbook { get; set; } = 10_000;

    public int MaxSheets { get; set; } = 10;
    public int MaxColumns { get; set; } = 64;
}

/// <summary>
/// Tool soạn file Office cho agent — trả kết quả dạng tệp tải được NGAY TRONG CHAT:
/// <list type="bullet">
///   <item><c>create_docx</c>: nhận JSON {fileName, title, markdown} (markdown tập con:
///   #/##/### tiêu đề, đoạn văn, - gạch đầu dòng, 1. đánh số, **đậm**, *nghiêng*,
///   bảng |a|b|) → file Word thật (OpenXML).</item>
///   <item><c>create_xlsx</c>: nhận JSON {fileName, sheets:[{name, headers, rows}]}
///   → workbook thật, hàng tiêu đề in đậm, số được ghi thành ô SỐ (lọc/tính được).</item>
/// </list>
/// File ghi vào kho upload cô lập của người dùng (tên lưu do server sinh — không
/// đoán được, không traversal) và đi ra ngoài bằng đúng đường <c>[FILE:]</c> marker
/// mà run_python đang dùng: client hiện thẻ tải, bytes phục vụ qua GET /api/files/{id}.
/// Mọi lỗi trả chuỗi "[Tài liệu] …" agent đọc được — không bao giờ ném ra ngoài.
/// </summary>
public sealed class OfficeAuthoringService
{
    private const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const int MaxFileNameLength = 100;

    private static readonly Regex InlineToken = new(@"(\*\*[^*]+\*\*|\*[^*]+\*)", RegexOptions.Compiled);

    private readonly UserResourceAccessService _resources;
    private readonly OfficeAuthoringOptions _options;
    private readonly ILogger<OfficeAuthoringService> _logger;

    public OfficeAuthoringService(
        UserResourceAccessService resources,
        IOptions<OfficeAuthoringOptions> options,
        ILogger<OfficeAuthoringService> logger)
    {
        _resources = resources;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => _options.Enabled;

    // ── create_docx ─────────────────────────────────────────────────────

    public (string Message, ProducedFile? File) CreateDocx(Guid tenantId, Guid userId, string input)
    {
        if (!IsEnabled) return ("[Tài liệu] Tool soạn file Office đang tắt (OfficeAuthoring:Enabled).", null);

        string? fileName = null, title = null, markdown;
        var trimmed = input?.Trim() ?? string.Empty;
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                fileName = GetString(doc.RootElement, "fileName");
                title = GetString(doc.RootElement, "title");
                markdown = GetString(doc.RootElement, "markdown") ?? GetString(doc.RootElement, "content");
            }
            catch (JsonException)
            {
                return ("[Tài liệu] JSON không hợp lệ. Định dạng: {\"fileName\":\"bao-cao.docx\",\"title\":\"…\",\"markdown\":\"# …\"}.", null);
            }
        }
        else
        {
            markdown = trimmed; // fallback: markdown trần → tên tệp mặc định
        }

        if (string.IsNullOrWhiteSpace(markdown))
            return ("[Tài liệu] Thiếu nội dung \"markdown\".", null);
        if (markdown.Length > _options.MaxContentChars)
            return ($"[Tài liệu] Nội dung vượt trần {_options.MaxContentChars} ký tự.", null);

        var safeName = SanitizeFileName(fileName, ".docx", "tai-lieu.docx");

        try
        {
            var (storedName, path) = ReserveStoredFile(tenantId, userId, ".docx");
            using (var wordDocument = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            {
                var mainPart = wordDocument.AddMainDocumentPart();
                var body = new W.Body();
                mainPart.Document = new W.Document(body);

                if (!string.IsNullOrWhiteSpace(title))
                    body.Append(HeadingParagraph(title.Trim(), sizeHalfPoints: 36, spacingAfter: 240));

                AppendMarkdown(body, markdown);
                mainPart.Document.Save();
            }

            return Success(safeName, storedName, path, DocxContentType, "Word");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "📄 [OfficeAuthoring] Không tạo được docx.");
            return ("[Tài liệu] Không tạo được file Word — nội dung có cấu trúc không xử lý được.", null);
        }
    }

    // ── create_xlsx ─────────────────────────────────────────────────────

    public (string Message, ProducedFile? File) CreateXlsx(Guid tenantId, Guid userId, string input)
    {
        if (!IsEnabled) return ("[Tài liệu] Tool soạn file Office đang tắt (OfficeAuthoring:Enabled).", null);

        var trimmed = input?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith('{'))
            return ("[Tài liệu] Payload phải là JSON: {\"fileName\":\"bang.xlsx\",\"sheets\":[{\"name\":\"Trang 1\",\"headers\":[…],\"rows\":[[…]]}]}.", null);

        string? fileName;
        List<(string Name, List<string>? Headers, List<List<JsonElement>> Rows)> sheets = [];
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            fileName = GetString(root, "fileName");
            if (!root.TryGetProperty("sheets", out var sheetsProp) || sheetsProp.ValueKind != JsonValueKind.Array)
                return ("[Tài liệu] Thiếu \"sheets\" (mảng các trang tính).", null);

            var totalRows = 0;
            foreach (var sheet in sheetsProp.EnumerateArray())
            {
                if (sheets.Count >= _options.MaxSheets)
                    return ($"[Tài liệu] Tối đa {_options.MaxSheets} trang tính.", null);

                var name = GetString(sheet, "name") ?? $"Trang {sheets.Count + 1}";
                List<string>? headers = null;
                if (sheet.TryGetProperty("headers", out var headersProp) && headersProp.ValueKind == JsonValueKind.Array)
                    headers = headersProp.EnumerateArray().Select(RenderCellText).ToList();

                var rows = new List<List<JsonElement>>();
                if (sheet.TryGetProperty("rows", out var rowsProp) && rowsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rowsProp.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Array) continue;
                        totalRows++;
                        if (totalRows > _options.MaxRowsPerWorkbook)
                            return ($"[Tài liệu] Vượt trần {_options.MaxRowsPerWorkbook} dòng dữ liệu cho một workbook.", null);
                        var cells = row.EnumerateArray().Take(_options.MaxColumns).Select(cell => cell.Clone()).ToList();
                        rows.Add(cells);
                    }
                }

                if ((headers?.Count ?? 0) > _options.MaxColumns)
                    return ($"[Tài liệu] Tối đa {_options.MaxColumns} cột.", null);
                sheets.Add((name, headers, rows));
            }
        }
        catch (JsonException)
        {
            return ("[Tài liệu] JSON không hợp lệ. Ví dụ: {\"fileName\":\"so-lieu.xlsx\",\"sheets\":[{\"name\":\"Q3\",\"headers\":[\"Chỉ tiêu\",\"Giá trị\"],\"rows\":[[\"pH\",7.2]]}]}.", null);
        }

        if (sheets.Count == 0)
            return ("[Tài liệu] Cần ít nhất một trang tính trong \"sheets\".", null);

        var safeName = SanitizeFileName(fileName, ".xlsx", "bang-tinh.xlsx");

        try
        {
            var (storedName, path) = ReserveStoredFile(tenantId, userId, ".xlsx");
            using (var spreadsheet = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
            {
                var workbookPart = spreadsheet.AddWorkbookPart();
                workbookPart.Workbook = new S.Workbook();
                AddMinimalStylesheet(workbookPart);

                var sheetList = new S.Sheets();
                workbookPart.Workbook.AppendChild(sheetList);

                uint sheetId = 1;
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, headers, rows) in sheets)
                {
                    var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                    var sheetData = new S.SheetData();
                    worksheetPart.Worksheet = new S.Worksheet(sheetData);

                    if (headers is { Count: > 0 })
                    {
                        var headerRow = new S.Row();
                        foreach (var header in headers)
                            headerRow.Append(TextCell(header, styleIndex: 1)); // in đậm
                        sheetData.Append(headerRow);
                    }

                    foreach (var row in rows)
                    {
                        var dataRow = new S.Row();
                        foreach (var cell in row)
                            dataRow.Append(DataCell(cell));
                        sheetData.Append(dataRow);
                    }

                    sheetList.Append(new S.Sheet
                    {
                        Id = workbookPart.GetIdOfPart(worksheetPart),
                        SheetId = sheetId++,
                        Name = SanitizeSheetName(name, usedNames)
                    });
                }

                workbookPart.Workbook.Save();
            }

            return Success(safeName, storedName, path, XlsxContentType, "Excel");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "📊 [OfficeAuthoring] Không tạo được xlsx.");
            return ("[Tài liệu] Không tạo được file Excel — dữ liệu có cấu trúc không xử lý được.", null);
        }
    }

    // ── markdown → Word ─────────────────────────────────────────────────

    private void AppendMarkdown(W.Body body, string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimEnd();

            if (trimmed.Length == 0) continue;

            // Bảng markdown: gom các dòng |…| liên tiếp thành MỘT bảng Word thật.
            if (trimmed.StartsWith('|') && trimmed.EndsWith("|"))
            {
                var tableLines = new List<string>();
                while (i < lines.Length && lines[i].TrimEnd() is { Length: > 0 } t && t.StartsWith('|') && t.EndsWith("|"))
                {
                    tableLines.Add(t);
                    i++;
                }
                i--; // vòng for sẽ ++ lại
                body.Append(BuildTable(tableLines));
                continue;
            }

            if (trimmed.StartsWith("### ")) { body.Append(HeadingParagraph(trimmed[4..], 26)); continue; }
            if (trimmed.StartsWith("## ")) { body.Append(HeadingParagraph(trimmed[3..], 28)); continue; }
            if (trimmed.StartsWith("# ")) { body.Append(HeadingParagraph(trimmed[2..], 32)); continue; }

            // Gạch đầu dòng / đánh số: v1 giữ ký hiệu dạng chữ + thụt lề treo — mở được
            // ở mọi trình đọc, không cần NumberingPart.
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                body.Append(ListParagraph("•  " + trimmed[2..]));
                continue;
            }
            var numbered = Regex.Match(trimmed, @"^(\d{1,3})\.\s+(.*)$");
            if (numbered.Success)
            {
                body.Append(ListParagraph($"{numbered.Groups[1].Value}.  {numbered.Groups[2].Value}"));
                continue;
            }

            body.Append(BodyParagraph(trimmed));
        }
    }

    private static W.Paragraph HeadingParagraph(string text, int sizeHalfPoints, int spacingAfter = 120)
    {
        var paragraph = new W.Paragraph(new W.ParagraphProperties(
            new W.SpacingBetweenLines { Before = "160", After = spacingAfter.ToString() }));
        foreach (var run in InlineRuns(text, forceBold: true, sizeHalfPoints)) paragraph.Append(run);
        return paragraph;
    }

    private static W.Paragraph BodyParagraph(string text)
    {
        var paragraph = new W.Paragraph(new W.ParagraphProperties(
            new W.SpacingBetweenLines { After = "120" }));
        foreach (var run in InlineRuns(text)) paragraph.Append(run);
        return paragraph;
    }

    private static W.Paragraph ListParagraph(string text)
    {
        var paragraph = new W.Paragraph(new W.ParagraphProperties(
            new W.SpacingBetweenLines { After = "60" },
            new W.Indentation { Left = "360", Hanging = "240" }));
        foreach (var run in InlineRuns(text)) paragraph.Append(run);
        return paragraph;
    }

    /// <summary>Tách **đậm** / *nghiêng* thành các run Word; phần còn lại là chữ thường.</summary>
    private static IEnumerable<W.Run> InlineRuns(string text, bool forceBold = false, int? sizeHalfPoints = null)
    {
        foreach (var piece in InlineToken.Split(text))
        {
            if (piece.Length == 0) continue;
            string content;
            bool bold = forceBold, italic = false;
            if (piece.StartsWith("**") && piece.EndsWith("**") && piece.Length > 4)
            {
                content = piece[2..^2];
                bold = true;
            }
            else if (piece.StartsWith('*') && piece.EndsWith("*") && piece.Length > 2)
            {
                content = piece[1..^1];
                italic = true;
            }
            else content = piece;

            var properties = new W.RunProperties();
            if (bold) properties.Append(new W.Bold());
            if (italic) properties.Append(new W.Italic());
            if (sizeHalfPoints is { } size) properties.Append(new W.FontSize { Val = size.ToString() });

            var run = new W.Run();
            if (properties.HasChildren) run.Append(properties);
            run.Append(new W.Text(content) { Space = SpaceProcessingModeValues.Preserve });
            yield return run;
        }
    }

    private static W.Table BuildTable(IReadOnlyList<string> tableLines)
    {
        var table = new W.Table(new W.TableProperties(
            new W.TableBorders(
                new W.TopBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.BottomBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.LeftBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.RightBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4 }),
            new W.TableWidth { Type = W.TableWidthUnitValues.Pct, Width = "5000" }));

        var isFirstContentRow = true;
        foreach (var line in tableLines)
        {
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();
            // Dòng phân cách |---|---| của markdown: bỏ qua.
            if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':' or ' '))) continue;

            var row = new W.TableRow();
            foreach (var cellText in cells)
            {
                var paragraph = new W.Paragraph();
                foreach (var run in InlineRuns(cellText, forceBold: isFirstContentRow)) paragraph.Append(run);
                row.Append(new W.TableCell(paragraph));
            }
            table.Append(row);
            isFirstContentRow = false;
        }
        return table;
    }

    // ── xlsx helpers ────────────────────────────────────────────────────

    /// <summary>Stylesheet tối thiểu: font 0 = thường, font 1 = đậm; cellXfs 1 dùng cho hàng tiêu đề.</summary>
    private static void AddMinimalStylesheet(WorkbookPart workbookPart)
    {
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = new S.Stylesheet(
            new S.Fonts(
                new S.Font(),
                new S.Font(new S.Bold())),
            new S.Fills(
                new S.Fill(new S.PatternFill { PatternType = S.PatternValues.None }),
                new S.Fill(new S.PatternFill { PatternType = S.PatternValues.Gray125 })),
            new S.Borders(new S.Border()),
            new S.CellFormats(
                new S.CellFormat(),
                new S.CellFormat { FontId = 1, ApplyFont = true }));
        stylesPart.Stylesheet.Save();
    }

    private static S.Cell TextCell(string text, uint styleIndex = 0) => new()
    {
        DataType = S.CellValues.InlineString,
        StyleIndex = styleIndex,
        InlineString = new S.InlineString(new S.Text(text) { Space = SpaceProcessingModeValues.Preserve })
    };

    /// <summary>Số JSON → ô SỐ thật (sort/sum được trong Excel); còn lại → chữ.</summary>
    private static S.Cell DataCell(JsonElement cell) => cell.ValueKind switch
    {
        JsonValueKind.Number => new S.Cell
        {
            DataType = S.CellValues.Number,
            CellValue = new S.CellValue(cell.GetRawText())
        },
        JsonValueKind.True or JsonValueKind.False => TextCell(cell.GetBoolean() ? "TRUE" : "FALSE"),
        JsonValueKind.Null or JsonValueKind.Undefined => TextCell(string.Empty),
        _ => TextCell(RenderCellText(cell))
    };

    private static string RenderCellText(JsonElement cell) =>
        cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? string.Empty : cell.GetRawText();

    private static string SanitizeSheetName(string name, HashSet<string> used)
    {
        var cleaned = new string(name.Where(ch => ch is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "Trang";
        if (cleaned.Length > 28) cleaned = cleaned[..28];
        var candidate = cleaned;
        var suffix = 2;
        while (!used.Add(candidate)) candidate = $"{cleaned}_{suffix++}";
        return candidate;
    }

    // ── chung ───────────────────────────────────────────────────────────

    /// <summary>Đặt chỗ tệp trong kho upload cô lập của người dùng — tên lưu do server sinh.</summary>
    private (string StoredName, string Path) ReserveStoredFile(Guid tenantId, Guid userId, string extension)
    {
        var uploadDir = _resources.GetUploadDirectory(tenantId, userId);
        Directory.CreateDirectory(uploadDir);
        var storedName = $"{Guid.NewGuid():N}{extension}";
        return (storedName, Path.Combine(uploadDir, storedName));
    }

    private (string Message, ProducedFile? File) Success(
        string displayName, string storedName, string path, string contentType, string kind)
    {
        var size = new FileInfo(path).Length;
        _logger.LogInformation("📄 [OfficeAuthoring] Đã tạo {Kind} {Name} ({Size} bytes).", kind, displayName, size);
        var file = new ProducedFile(storedName, displayName, contentType, size);
        return ($"[Tài liệu] Đã tạo file {kind} \"{displayName}\" ({FormatSize(size)}) — tệp đính kèm trong câu trả lời để người dùng tải về.", file);
    }

    /// <summary>Tên hiển thị an toàn: bỏ đường dẫn/ký tự cấm, ép đúng đuôi, có mặc định.</summary>
    private static string SanitizeFileName(string? requested, string requiredExtension, string fallback)
    {
        var name = Path.GetFileName(requested?.Trim() ?? string.Empty);
        name = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        if (!name.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
            name += requiredExtension;
        return name.Length <= MaxFileNameLength ? name : name[^MaxFileNameLength..];
    }

    private static string FormatSize(long bytes) => bytes >= 1_048_576
        ? $"{bytes / 1_048_576.0:0.#} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
}
