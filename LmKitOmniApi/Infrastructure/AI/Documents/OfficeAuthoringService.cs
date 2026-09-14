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
/// soạn tài liệu thuần local (không model, không mạng), ghi vào kho upload cô
/// lập của chính người dùng, có trần kích thước.
/// </summary>
public sealed class OfficeAuthoringOptions
{
    public const string SectionName = "OfficeAuthoring";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Engine dựng file: "Aspose" (mặc định — Words/Cells 20.10 có license, đủ
    /// heading style thật, list thật, header/footer, TOC, công thức, chart, PDF)
    /// hoặc "OpenXml" (MIT, cơ bản). Engine Aspose hỏng vì môi trường (thiếu
    /// GDI/Skia trên Linux) thì docx/xlsx TỰ RƠI VỀ OpenXml — người dùng vẫn có
    /// file; riêng PDF không có fallback nên trả thông điệp rõ ràng.
    /// </summary>
    public string Engine { get; set; } = "Aspose";

    /// <summary>
    /// Đường dẫn file .lic Aspose (Total/Words+Cells). KHÔNG commit vào repo —
    /// mount/copy khi triển khai. Trống/sai → Aspose chạy chế độ đánh giá
    /// (watermark) và thông điệp trả về có cảnh báo.
    /// </summary>
    public string? AsposeLicensePath { get; set; }

    /// <summary>Trần ký tự phần nội dung markdown của create_docx / create_pdf.</summary>
    public int MaxContentChars { get; set; } = 200_000;

    /// <summary>Trần tổng số dòng dữ liệu của một workbook create_xlsx.</summary>
    public int MaxRowsPerWorkbook { get; set; } = 10_000;

    public int MaxSheets { get; set; } = 10;
    public int MaxColumns { get; set; } = 64;
}

/// <summary>
/// Tool soạn file Office cho agent — trả kết quả dạng tệp tải được NGAY TRONG CHAT
/// (đường <c>[FILE:]</c> marker + GET /api/files/{id}, như run_python):
/// <list type="bullet">
///   <item><c>create_docx</c> / <c>create_pdf</c>: JSON {fileName, title, markdown,
///   options{fontName,fontSize,lineSpacing,header,footer,pageNumbers,toc}} —
///   markdown tập con: #/##/### tiêu đề, đoạn văn, -/1. danh sách, **đậm**,
///   *nghiêng*, bảng |a|b|.</item>
///   <item><c>create_xlsx</c>: JSON {fileName, sheets:[{name, headers, rows,
///   freezeHeader, columnWidths, columnFormats, chart{type,title,categoryColumn,
///   seriesColumns}}]} — số là ô số thật, ô "=…" là công thức thật.</item>
/// </list>
/// Payload được parse MỘT lần thành spec rồi đưa cho engine (Aspose chính /
/// OpenXml fallback) — hai engine không bao giờ hiểu payload khác nhau. Mọi lỗi
/// trả chuỗi "[Tài liệu] …" agent đọc được — không bao giờ ném ra ngoài.
/// </summary>
public sealed class OfficeAuthoringService
{
    private const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string PdfContentType = "application/pdf";
    private const int MaxFileNameLength = 100;
    private const string AsposeEngineName = "Aspose";

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

    private bool UseAspose => string.Equals(_options.Engine, AsposeEngineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>create_pdf chỉ tồn tại trên engine Aspose (OpenXml không render PDF).</summary>
    public bool IsPdfAvailable => IsEnabled && UseAspose;

    // ── create_docx ─────────────────────────────────────────────────────

    public (string Message, ProducedFile? File) CreateDocx(Guid tenantId, Guid userId, string input)
    {
        if (!IsEnabled) return ("[Tài liệu] Tool soạn file Office đang tắt (OfficeAuthoring:Enabled).", null);

        var (spec, error) = ParseDocxSpec(input);
        if (error is not null) return (error, null);

        var safeName = SanitizeFileName(spec!.FileName, ".docx", "tai-lieu.docx");
        var (storedName, path) = ReserveStoredFile(tenantId, userId, ".docx");

        if (UseAspose)
        {
            var licensed = AsposeLicensing.EnsureApplied(_options.AsposeLicensePath, _logger);
            try
            {
                AsposeOfficeEngine.BuildDocx(spec, path);
                return Success(safeName, storedName, path, DocxContentType, "Word", licensed ? null : EvaluationNote);
            }
            catch (Exception ex)
            {
                // Môi trường thiếu GDI/Skia (Linux) hoặc lỗi engine: rơi về OpenXml —
                // người dùng vẫn nhận được file, chỉ mất phần trình bày nâng cao.
                _logger.LogWarning(ex, "📄 [OfficeAuthoring] Aspose docx thất bại — rơi về engine OpenXml.");
            }
        }

        try
        {
            BuildDocxOpenXml(spec, path);
            var note = UseAspose ? " (engine dự phòng OpenXml — không header/footer/TOC)" : null;
            return Success(safeName, storedName, path, DocxContentType, "Word", note);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "📄 [OfficeAuthoring] Không tạo được docx.");
            TryDeleteFile(path);
            return ("[Tài liệu] Không tạo được file Word — nội dung có cấu trúc không xử lý được.", null);
        }
    }

    // ── create_pdf (Aspose only) ────────────────────────────────────────

    public (string Message, ProducedFile? File) CreatePdf(Guid tenantId, Guid userId, string input)
    {
        if (!IsEnabled) return ("[Tài liệu] Tool soạn file Office đang tắt (OfficeAuthoring:Enabled).", null);
        if (!UseAspose)
            return ("[Tài liệu] Xuất PDF cần engine Aspose (OfficeAuthoring:Engine=Aspose). Hãy tạo file Word bằng create_docx thay thế.", null);

        var (spec, error) = ParseDocxSpec(input);
        if (error is not null) return (error, null);

        var safeName = SanitizeFileName(spec!.FileName, ".pdf", "tai-lieu.pdf");
        var (storedName, path) = ReserveStoredFile(tenantId, userId, ".pdf");
        var licensed = AsposeLicensing.EnsureApplied(_options.AsposeLicensePath, _logger);

        try
        {
            AsposeOfficeEngine.BuildPdf(spec, path);
            return Success(safeName, storedName, path, PdfContentType, "PDF", licensed ? null : EvaluationNote);
        }
        catch (Exception ex)
        {
            // Render PDF cần đo chữ (SkiaSharp/GDI) — môi trường thiếu sẽ rơi vào đây.
            _logger.LogWarning(ex, "📄 [OfficeAuthoring] Aspose PDF thất bại trên môi trường này.");
            TryDeleteFile(path);
            return ("[Tài liệu] Không xuất được PDF trên môi trường máy chủ hiện tại (thiếu engine render chữ). "
                + "Hãy tạo file Word bằng create_docx — nội dung giữ nguyên.", null);
        }
    }

    // ── create_xlsx ─────────────────────────────────────────────────────

    public (string Message, ProducedFile? File) CreateXlsx(Guid tenantId, Guid userId, string input)
    {
        if (!IsEnabled) return ("[Tài liệu] Tool soạn file Office đang tắt (OfficeAuthoring:Enabled).", null);

        var (spec, error) = ParseXlsxSpec(input);
        if (error is not null) return (error, null);

        var safeName = SanitizeFileName(spec!.FileName, ".xlsx", "bang-tinh.xlsx");
        var (storedName, path) = ReserveStoredFile(tenantId, userId, ".xlsx");

        if (UseAspose)
        {
            var licensed = AsposeLicensing.EnsureApplied(_options.AsposeLicensePath, _logger);
            try
            {
                AsposeOfficeEngine.BuildXlsx(spec, path);
                return Success(safeName, storedName, path, XlsxContentType, "Excel", licensed ? null : EvaluationNote);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "📊 [OfficeAuthoring] Aspose xlsx thất bại — rơi về engine OpenXml.");
            }
        }

        try
        {
            BuildXlsxOpenXml(spec, path);
            var note = UseAspose ? " (engine dự phòng OpenXml — không công thức/chart/định dạng cột)" : null;
            return Success(safeName, storedName, path, XlsxContentType, "Excel", note);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "📊 [OfficeAuthoring] Không tạo được xlsx.");
            TryDeleteFile(path);
            return ("[Tài liệu] Không tạo được file Excel — dữ liệu có cấu trúc không xử lý được.", null);
        }
    }

    private const string EvaluationNote =
        " ⚠️ Chưa nạp license Aspose (OfficeAuthoring:AsposeLicensePath) — file mang watermark bản đánh giá.";

    // ── parse: payload → spec (một nguồn sự thật cho cả hai engine) ─────

    private (DocxSpec? Spec, string? Error) ParseDocxSpec(string input)
    {
        string? fileName = null, title = null, markdown;
        var optionsSpec = new DocxOptionsSpec();

        var trimmed = input?.Trim() ?? string.Empty;
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                fileName = GetString(root, "fileName");
                title = GetString(root, "title");
                markdown = GetString(root, "markdown") ?? GetString(root, "content");

                if (root.TryGetProperty("options", out var opt) && opt.ValueKind == JsonValueKind.Object)
                {
                    optionsSpec = new DocxOptionsSpec
                    {
                        FontName = GetString(opt, "fontName") ?? "Times New Roman",
                        FontSize = GetDouble(opt, "fontSize") ?? 13,
                        LineSpacing = Math.Clamp(GetDouble(opt, "lineSpacing") ?? 1.5, 1, 3),
                        Header = GetString(opt, "header"),
                        Footer = GetString(opt, "footer"),
                        PageNumbers = GetBool(opt, "pageNumbers") ?? false,
                        Toc = GetBool(opt, "toc") ?? false
                    };
                }
            }
            catch (JsonException)
            {
                return (null, "[Tài liệu] JSON không hợp lệ. Định dạng: {\"fileName\":\"bao-cao.docx\",\"title\":\"…\",\"markdown\":\"# …\",\"options\":{\"pageNumbers\":true}}.");
            }
        }
        else
        {
            markdown = trimmed; // fallback: markdown trần → tên tệp mặc định
        }

        if (string.IsNullOrWhiteSpace(markdown))
            return (null, "[Tài liệu] Thiếu nội dung \"markdown\".");
        if (markdown.Length > _options.MaxContentChars)
            return (null, $"[Tài liệu] Nội dung vượt trần {_options.MaxContentChars} ký tự.");

        return (new DocxSpec { FileName = fileName, Title = title, Markdown = markdown, Options = optionsSpec }, null);
    }

    private (XlsxSpec? Spec, string? Error) ParseXlsxSpec(string input)
    {
        var trimmed = input?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith('{'))
            return (null, "[Tài liệu] Payload phải là JSON: {\"fileName\":\"bang.xlsx\",\"sheets\":[{\"name\":\"Trang 1\",\"headers\":[…],\"rows\":[[…]]}]}.");

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            var fileName = GetString(root, "fileName");
            if (!root.TryGetProperty("sheets", out var sheetsProp) || sheetsProp.ValueKind != JsonValueKind.Array)
                return (null, "[Tài liệu] Thiếu \"sheets\" (mảng các trang tính).");

            var sheets = new List<XlsxSheetSpec>();
            var totalRows = 0;
            foreach (var sheet in sheetsProp.EnumerateArray())
            {
                if (sheets.Count >= _options.MaxSheets)
                    return (null, $"[Tài liệu] Tối đa {_options.MaxSheets} trang tính.");

                List<string>? headers = null;
                if (sheet.TryGetProperty("headers", out var headersProp) && headersProp.ValueKind == JsonValueKind.Array)
                    headers = headersProp.EnumerateArray().Select(RenderCellText).ToList();
                if ((headers?.Count ?? 0) > _options.MaxColumns)
                    return (null, $"[Tài liệu] Tối đa {_options.MaxColumns} cột.");

                var rows = new List<List<JsonElement>>();
                if (sheet.TryGetProperty("rows", out var rowsProp) && rowsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rowsProp.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Array) continue;
                        totalRows++;
                        if (totalRows > _options.MaxRowsPerWorkbook)
                            return (null, $"[Tài liệu] Vượt trần {_options.MaxRowsPerWorkbook} dòng dữ liệu cho một workbook.");
                        rows.Add(row.EnumerateArray().Take(_options.MaxColumns).Select(cell => cell.Clone()).ToList());
                    }
                }

                XlsxChartSpec? chart = null;
                if (sheet.TryGetProperty("chart", out var chartProp) && chartProp.ValueKind == JsonValueKind.Object)
                {
                    var seriesColumns = new List<int>();
                    if (chartProp.TryGetProperty("seriesColumns", out var seriesProp) && seriesProp.ValueKind == JsonValueKind.Array)
                    {
                        seriesColumns = seriesProp.EnumerateArray()
                            .Where(x => x.ValueKind == JsonValueKind.Number)
                            .Select(x => x.GetInt32())
                            .Take(8)
                            .ToList();
                    }
                    chart = new XlsxChartSpec
                    {
                        Type = GetString(chartProp, "type") ?? "column",
                        Title = GetString(chartProp, "title"),
                        CategoryColumn = (int)(GetDouble(chartProp, "categoryColumn") ?? 0),
                        SeriesColumns = seriesColumns.Count > 0 ? seriesColumns : [1]
                    };
                }

                List<double>? columnWidths = null;
                if (sheet.TryGetProperty("columnWidths", out var widthsProp) && widthsProp.ValueKind == JsonValueKind.Array)
                {
                    columnWidths = widthsProp.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.Number)
                        .Select(x => Math.Clamp(x.GetDouble(), 4, 120))
                        .ToList();
                }

                List<string?>? columnFormats = null;
                if (sheet.TryGetProperty("columnFormats", out var formatsProp) && formatsProp.ValueKind == JsonValueKind.Array)
                {
                    columnFormats = formatsProp.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
                        .ToList();
                }

                sheets.Add(new XlsxSheetSpec
                {
                    Name = GetString(sheet, "name") ?? $"Trang {sheets.Count + 1}",
                    Headers = headers,
                    Rows = rows,
                    FreezeHeader = GetBool(sheet, "freezeHeader") ?? true,
                    ColumnWidths = columnWidths,
                    ColumnFormats = columnFormats,
                    Chart = chart
                });
            }

            if (sheets.Count == 0)
                return (null, "[Tài liệu] Cần ít nhất một trang tính trong \"sheets\".");

            return (new XlsxSpec { FileName = fileName, Sheets = sheets }, null);
        }
        catch (JsonException)
        {
            return (null, "[Tài liệu] JSON không hợp lệ. Ví dụ: {\"fileName\":\"so-lieu.xlsx\",\"sheets\":[{\"name\":\"Q3\",\"headers\":[\"Chỉ tiêu\",\"Giá trị\"],\"rows\":[[\"pH\",7.2]]}]}.");
        }
    }

    // ── engine OpenXml (fallback / Engine=OpenXml) ──────────────────────

    private static void BuildDocxOpenXml(DocxSpec spec, string path)
    {
        using var wordDocument = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = wordDocument.AddMainDocumentPart();
        var body = new W.Body();
        mainPart.Document = new W.Document(body);

        if (!string.IsNullOrWhiteSpace(spec.Title))
            body.Append(HeadingParagraph(spec.Title.Trim(), sizeHalfPoints: 36, spacingAfter: 240));

        AppendMarkdownOpenXml(body, spec.Markdown);
        mainPart.Document.Save();
    }

    private static void AppendMarkdownOpenXml(W.Body body, string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd();
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
                i--;
                body.Append(BuildTableOpenXml(tableLines));
                continue;
            }

            if (trimmed.StartsWith("### ")) { body.Append(HeadingParagraph(trimmed[4..], 26)); continue; }
            if (trimmed.StartsWith("## ")) { body.Append(HeadingParagraph(trimmed[3..], 28)); continue; }
            if (trimmed.StartsWith("# ")) { body.Append(HeadingParagraph(trimmed[2..], 32)); continue; }

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

    private static W.Table BuildTableOpenXml(IReadOnlyList<string> tableLines)
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

    private static void BuildXlsxOpenXml(XlsxSpec spec, string path)
    {
        using var spreadsheet = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = spreadsheet.AddWorkbookPart();
        workbookPart.Workbook = new S.Workbook();
        AddMinimalStylesheet(workbookPart);

        var sheetList = new S.Sheets();
        workbookPart.Workbook.AppendChild(sheetList);

        uint sheetId = 1;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheetSpec in spec.Sheets)
        {
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new S.SheetData();
            worksheetPart.Worksheet = new S.Worksheet(sheetData);

            if (sheetSpec.Headers is { Count: > 0 })
            {
                var headerRow = new S.Row();
                foreach (var header in sheetSpec.Headers)
                    headerRow.Append(TextCell(header, styleIndex: 1)); // in đậm
                sheetData.Append(headerRow);
            }

            foreach (var row in sheetSpec.Rows)
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
                Name = SanitizeSheetName(sheetSpec.Name, usedNames)
            });
        }

        workbookPart.Workbook.Save();
    }

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
        string displayName, string storedName, string path, string contentType, string kind, string? note)
    {
        var size = new FileInfo(path).Length;
        _logger.LogInformation("📄 [OfficeAuthoring] Đã tạo {Kind} {Name} ({Size} bytes).", kind, displayName, size);
        var file = new ProducedFile(storedName, displayName, contentType, size);
        return ($"[Tài liệu] Đã tạo file {kind} \"{displayName}\" ({FormatSize(size)}) — tệp đính kèm trong câu trả lời để người dùng tải về.{note}", file);
    }

    /// <summary>Tên hiển thị an toàn: bỏ đường dẫn/ký tự cấm, ép đúng đuôi, có mặc định.</summary>
    private static string SanitizeFileName(string? requested, string requiredExtension, string fallback)
    {
        var name = Path.GetFileName(requested?.Trim() ?? string.Empty);
        name = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        if (!name.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
        {
            // Đổi đuôi khi agent lỡ đặt đuôi khác (vd .docx cho create_pdf).
            var stem = Path.GetFileNameWithoutExtension(name);
            name = (string.IsNullOrWhiteSpace(stem) ? "tai-lieu" : stem) + requiredExtension;
        }
        return name.Length <= MaxFileNameLength ? name : name[^MaxFileNameLength..];
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private static string FormatSize(long bytes) => bytes >= 1_048_576
        ? $"{bytes / 1_048_576.0:0.#} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

    private static double? GetDouble(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number ? prop.GetDouble() : null;

    private static bool? GetBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean()
            : null;
}
