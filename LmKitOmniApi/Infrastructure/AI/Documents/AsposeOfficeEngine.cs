using System.Text.Json;
using System.Text.RegularExpressions;
using AC = Aspose.Cells;
using AW = Aspose.Words;

namespace LmKitOmniApi.Infrastructure.AI.Documents;

/// <summary>
/// Engine Aspose 20.10 (license thương mại) dựng file Office từ spec đã parse —
/// giàu hơn engine OpenXml fallback ở đúng những chỗ Aspose đáng tiền:
/// <list type="bullet">
///   <item><b>Word</b>: style Heading THẬT (TOC nhặt được), bullet/numbered list
///   THẬT (Tab xuống cấp được), page setup A4 + lề công văn VN, header/footer +
///   số trang (field PAGE/NUMPAGES), mục lục tự động, font mặc định Times New
///   Roman 13, giãn dòng 1.5.</item>
///   <item><b>Excel</b>: hàng tiêu đề đậm + nền màu + đóng băng, công thức
///   (ô bắt đầu bằng "=") có CalculateFormula, định dạng số theo cột, độ rộng
///   cột TỰ ƯỚC LƯỢNG bằng số học (cố ý không AutoFit — AutoFit đo chữ qua GDI,
///   chết trên Linux), biểu đồ column/line/pie cạnh vùng dữ liệu.</item>
///   <item><b>PDF</b>: cùng spec Word, save thẳng SaveFormat.Pdf — cần engine
///   render chữ (SkiaSharp/GDI); caller bắt PlatformNotSupported để báo rõ.</item>
/// </list>
/// Mọi method THROW khi hỏng — <see cref="OfficeAuthoringService"/> là nơi quyết
/// định fallback OpenXml hay trả thông điệp lỗi cho agent.
/// </summary>
public static class AsposeOfficeEngine
{
    private static readonly Regex InlineToken = new(@"(\*\*[^*]+\*\*|\*[^*]+\*)", RegexOptions.Compiled);

    // ── WORD ────────────────────────────────────────────────────────────

    public static void BuildDocx(DocxSpec spec, string path)
        => BuildWordDocument(spec).Save(path, AW.SaveFormat.Docx);

    public static void BuildPdf(DocxSpec spec, string path)
        => BuildWordDocument(spec).Save(path, AW.SaveFormat.Pdf);

    private static AW.Document BuildWordDocument(DocxSpec spec)
    {
        var document = new AW.Document();
        var builder = new AW.DocumentBuilder(document);
        var options = spec.Options;

        // Font + giãn dòng mặc định toàn tài liệu (kể cả style Heading để một font
        // thống nhất kiểu công văn — Heading giữ cỡ chữ riêng của nó).
        var normal = document.Styles[AW.StyleIdentifier.Normal];
        normal.Font.Name = options.FontName;
        normal.Font.Size = options.FontSize;
        normal.ParagraphFormat.LineSpacingRule = AW.LineSpacingRule.Multiple;
        normal.ParagraphFormat.LineSpacing = 12 * options.LineSpacing; // quy ước Multiple: 12pt = 1 dòng
        normal.ParagraphFormat.SpaceAfter = 6;
        foreach (var heading in new[]
                 {
                     AW.StyleIdentifier.Heading1, AW.StyleIdentifier.Heading2, AW.StyleIdentifier.Heading3
                 })
        {
            document.Styles[heading].Font.Name = options.FontName;
        }

        // Khổ giấy + lề công văn VN: trái 30mm, phải 20mm, trên/dưới 20mm.
        var pageSetup = builder.PageSetup;
        pageSetup.PaperSize = AW.PaperSize.A4;
        pageSetup.LeftMargin = AW.ConvertUtil.MillimeterToPoint(30);
        pageSetup.RightMargin = AW.ConvertUtil.MillimeterToPoint(20);
        pageSetup.TopMargin = AW.ConvertUtil.MillimeterToPoint(20);
        pageSetup.BottomMargin = AW.ConvertUtil.MillimeterToPoint(20);

        // Header/footer + số trang (field PAGE/NUMPAGES thật — tự cập nhật khi in).
        if (!string.IsNullOrWhiteSpace(options.Header))
        {
            builder.MoveToHeaderFooter(AW.HeaderFooterType.HeaderPrimary);
            builder.ParagraphFormat.Alignment = AW.ParagraphAlignment.Center;
            builder.Font.Size = Math.Max(9, options.FontSize - 2);
            builder.Font.Italic = true;
            builder.Write(options.Header.Trim());
            builder.Font.Italic = false;
        }
        if (options.PageNumbers || !string.IsNullOrWhiteSpace(options.Footer))
        {
            builder.MoveToHeaderFooter(AW.HeaderFooterType.FooterPrimary);
            builder.ParagraphFormat.Alignment = AW.ParagraphAlignment.Center;
            builder.Font.Size = Math.Max(9, options.FontSize - 2);
            if (!string.IsNullOrWhiteSpace(options.Footer))
            {
                builder.Write(options.Footer.Trim());
                if (options.PageNumbers) builder.Write(" — ");
            }
            if (options.PageNumbers)
            {
                builder.Write("Trang ");
                builder.InsertField(AW.Fields.FieldType.FieldPage, false);
                builder.Write(" / ");
                builder.InsertField(AW.Fields.FieldType.FieldNumPages, false);
            }
        }
        builder.MoveToDocumentEnd();

        if (!string.IsNullOrWhiteSpace(spec.Title))
        {
            builder.ParagraphFormat.StyleIdentifier = AW.StyleIdentifier.Title;
            builder.ParagraphFormat.Alignment = AW.ParagraphAlignment.Center;
            builder.Font.Name = options.FontName;
            builder.Writeln(spec.Title.Trim());
            builder.ParagraphFormat.StyleIdentifier = AW.StyleIdentifier.Normal;
            builder.ParagraphFormat.Alignment = AW.ParagraphAlignment.Left;
        }

        if (options.Toc)
        {
            // \o "1-3": lấy Heading1-3; \h: hyperlink; \z \u: chuẩn Word.
            builder.Writeln("MỤC LỤC");
            builder.InsertTableOfContents("\\o \"1-3\" \\h \\z \\u");
            builder.InsertBreak(AW.BreakType.PageBreak);
        }

        AppendMarkdown(builder, spec.Markdown);

        if (options.Toc)
        {
            // Điền entry + số trang cho TOC. Cần engine layout đo trang — trên máy
            // thiếu GDI/Skia sẽ ném: nuốt tại đây, TOC vẫn còn (Word tự F9 khi mở).
            try { document.UpdateFields(); } catch { /* TOC cập nhật khi người dùng mở file */ }
        }

        return document;
    }

    private static void AppendMarkdown(AW.DocumentBuilder builder, string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inList = false;

        void EndList()
        {
            if (!inList) return;
            builder.ListFormat.RemoveNumbers();
            inList = false;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd();
            if (trimmed.Length == 0) { EndList(); continue; }

            // Bảng markdown → bảng Word thật (viền đơn, hàng đầu đậm, full width).
            if (trimmed.StartsWith('|') && trimmed.EndsWith("|"))
            {
                EndList();
                var tableLines = new List<string>();
                while (i < lines.Length && lines[i].TrimEnd() is { Length: > 0 } t && t.StartsWith('|') && t.EndsWith("|"))
                {
                    tableLines.Add(t);
                    i++;
                }
                i--;
                BuildTable(builder, tableLines);
                continue;
            }

            if (trimmed.StartsWith("### ")) { EndList(); WriteHeading(builder, AW.StyleIdentifier.Heading3, trimmed[4..]); continue; }
            if (trimmed.StartsWith("## ")) { EndList(); WriteHeading(builder, AW.StyleIdentifier.Heading2, trimmed[3..]); continue; }
            if (trimmed.StartsWith("# ")) { EndList(); WriteHeading(builder, AW.StyleIdentifier.Heading1, trimmed[2..]); continue; }

            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                if (!inList) { builder.ListFormat.ApplyBulletDefault(); inList = true; }
                WriteInline(builder, trimmed[2..]);
                builder.Writeln();
                continue;
            }
            var numbered = Regex.Match(trimmed, @"^\d{1,3}\.\s+(.*)$");
            if (numbered.Success)
            {
                if (!inList) { builder.ListFormat.ApplyNumberDefault(); inList = true; }
                WriteInline(builder, numbered.Groups[1].Value);
                builder.Writeln();
                continue;
            }

            EndList();
            WriteInline(builder, trimmed);
            builder.Writeln();
        }
        EndList();
    }

    private static void WriteHeading(AW.DocumentBuilder builder, AW.StyleIdentifier style, string text)
    {
        builder.ParagraphFormat.StyleIdentifier = style;
        WriteInline(builder, text);
        builder.Writeln();
        builder.ParagraphFormat.StyleIdentifier = AW.StyleIdentifier.Normal;
    }

    /// <summary>**đậm** / *nghiêng* → toggle Font.Bold/Italic quanh từng mảnh.</summary>
    private static void WriteInline(AW.DocumentBuilder builder, string text)
    {
        foreach (var piece in InlineToken.Split(text))
        {
            if (piece.Length == 0) continue;
            if (piece.StartsWith("**") && piece.EndsWith("**") && piece.Length > 4)
            {
                builder.Font.Bold = true;
                builder.Write(piece[2..^2]);
                builder.Font.Bold = false;
            }
            else if (piece.StartsWith('*') && piece.EndsWith("*") && piece.Length > 2)
            {
                builder.Font.Italic = true;
                builder.Write(piece[1..^1]);
                builder.Font.Italic = false;
            }
            else
            {
                builder.Write(piece);
            }
        }
    }

    private static void BuildTable(AW.DocumentBuilder builder, IReadOnlyList<string> tableLines)
    {
        var table = builder.StartTable();
        var isFirstContentRow = true;
        foreach (var line in tableLines)
        {
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();
            if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':' or ' '))) continue; // dòng |---|

            foreach (var cellText in cells)
            {
                builder.InsertCell();
                if (isFirstContentRow) builder.Font.Bold = true;
                WriteInline(builder, cellText);
                if (isFirstContentRow) builder.Font.Bold = false;
            }
            builder.EndRow();
            isFirstContentRow = false;
        }
        builder.EndTable();
        table.SetBorders(AW.LineStyle.Single, 0.75, System.Drawing.Color.Black);
        table.PreferredWidth = AW.Tables.PreferredWidth.FromPercent(100);
    }

    // ── EXCEL ───────────────────────────────────────────────────────────

    public static void BuildXlsx(XlsxSpec spec, string path)
    {
        using var workbook = new AC.Workbook();
        workbook.Worksheets.Clear();

        var headerStyle = workbook.CreateStyle();
        headerStyle.Font.IsBold = true;
        headerStyle.ForegroundColor = System.Drawing.Color.FromArgb(217, 225, 242);
        headerStyle.Pattern = AC.BackgroundType.Solid;
        headerStyle.SetBorder(AC.BorderType.BottomBorder, AC.CellBorderType.Thin, System.Drawing.Color.Gray);

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheetSpec in spec.Sheets)
        {
            var worksheet = workbook.Worksheets.Add(SanitizeSheetName(sheetSpec.Name, usedNames));
            var cells = worksheet.Cells;
            var rowIndex = 0;

            if (sheetSpec.Headers is { Count: > 0 })
            {
                for (var c = 0; c < sheetSpec.Headers.Count; c++)
                {
                    var cell = cells[0, c];
                    cell.PutValue(sheetSpec.Headers[c]);
                    cell.SetStyle(headerStyle);
                }
                rowIndex = 1;
                if (sheetSpec.FreezeHeader) worksheet.FreezePanes(1, 0, 1, 0);
            }

            foreach (var row in sheetSpec.Rows)
            {
                for (var c = 0; c < row.Count; c++)
                    PutCell(cells[rowIndex, c], row[c]);
                rowIndex++;
            }

            ApplyColumnFormatsAndWidths(workbook, worksheet, sheetSpec);

            if (sheetSpec.Chart is { } chartSpec && sheetSpec.Rows.Count > 0)
                AddChart(worksheet, sheetSpec, chartSpec);
        }

        // Công thức (ô "=…") được tính sẵn để giá trị cache đúng ngay khi mở file.
        workbook.CalculateFormula();
        workbook.Save(path, AC.SaveFormat.Xlsx);
    }

    private static void PutCell(AC.Cell cell, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                cell.PutValue(value.GetDouble());
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                cell.PutValue(value.GetBoolean());
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                break;
            case JsonValueKind.String when value.GetString() is { } s && s.StartsWith('='):
                cell.Formula = s; // công thức thật — CalculateFormula tính trước khi save
                break;
            default:
                cell.PutValue(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
                break;
        }
    }

    /// <summary>
    /// Định dạng số theo cột + độ rộng cột. Cố ý KHÔNG dùng AutoFitColumns:
    /// AutoFit đo chữ qua GDI (PlatformNotSupported trên Linux .NET 10) — ước
    /// lượng số học từ độ dài text là đủ tốt và chạy được mọi nơi.
    /// </summary>
    private static void ApplyColumnFormatsAndWidths(AC.Workbook workbook, AC.Worksheet worksheet, XlsxSheetSpec sheetSpec)
    {
        var columnCount = Math.Max(
            sheetSpec.Headers?.Count ?? 0,
            sheetSpec.Rows.Count == 0 ? 0 : sheetSpec.Rows.Max(r => r.Count));

        for (var c = 0; c < columnCount; c++)
        {
            if (sheetSpec.ColumnFormats is { } formats && c < formats.Count && !string.IsNullOrWhiteSpace(formats[c]))
            {
                var style = workbook.CreateStyle();
                style.Custom = formats[c]!;
                worksheet.Cells.Columns[c].ApplyStyle(style, new AC.StyleFlag { NumberFormat = true });
            }

            double width;
            if (sheetSpec.ColumnWidths is { } widths && c < widths.Count && widths[c] > 0)
            {
                width = widths[c];
            }
            else
            {
                var headerLength = sheetSpec.Headers is { } h && c < h.Count ? h[c].Length : 0;
                var contentLength = sheetSpec.Rows.Count == 0
                    ? 0
                    : sheetSpec.Rows.Max(r => c < r.Count ? RenderLength(r[c]) : 0);
                width = Math.Clamp(Math.Max(headerLength, contentLength) + 3, 10, 60);
            }
            worksheet.Cells.SetColumnWidth(c, width);
        }
    }

    private static int RenderLength(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()?.Length ?? 0,
        JsonValueKind.Null or JsonValueKind.Undefined => 0,
        _ => value.GetRawText().Length
    };

    private static void AddChart(AC.Worksheet worksheet, XlsxSheetSpec sheetSpec, XlsxChartSpec chartSpec)
    {
        var chartType = chartSpec.Type.Trim().ToLowerInvariant() switch
        {
            "line" => AC.Charts.ChartType.Line,
            "pie" => AC.Charts.ChartType.Pie,
            _ => AC.Charts.ChartType.Column
        };

        var dataColumns = Math.Max(sheetSpec.Headers?.Count ?? 0, sheetSpec.Rows.Max(r => r.Count));
        var firstDataRow = sheetSpec.Headers is { Count: > 0 } ? 2 : 1; // A1-based
        var lastDataRow = firstDataRow + sheetSpec.Rows.Count - 1;

        // Đặt biểu đồ bên PHẢI vùng dữ liệu, không đè số liệu.
        var chartIndex = worksheet.Charts.Add(chartType, 1, dataColumns + 1, 16, dataColumns + 9);
        var chart = worksheet.Charts[chartIndex];
        if (!string.IsNullOrWhiteSpace(chartSpec.Title))
            chart.Title.Text = chartSpec.Title;

        foreach (var seriesColumn in chartSpec.SeriesColumns.Where(sc => sc >= 0 && sc < dataColumns))
        {
            var column = ColumnLetter(seriesColumn);
            chart.NSeries.Add($"{column}{firstDataRow}:{column}{lastDataRow}", true);
        }
        if (chart.NSeries.Count > 0 && chartSpec.CategoryColumn >= 0 && chartSpec.CategoryColumn < dataColumns)
        {
            var categoryLetter = ColumnLetter(chartSpec.CategoryColumn);
            chart.NSeries.CategoryData = $"{categoryLetter}{firstDataRow}:{categoryLetter}{lastDataRow}";
        }
    }

    private static string ColumnLetter(int index)
    {
        var letters = string.Empty;
        index++;
        while (index > 0)
        {
            var rem = (index - 1) % 26;
            letters = (char)('A' + rem) + letters;
            index = (index - 1) / 26;
        }
        return letters;
    }

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
}
