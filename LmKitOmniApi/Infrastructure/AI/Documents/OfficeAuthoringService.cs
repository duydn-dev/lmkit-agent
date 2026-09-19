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
    /// Đường dẫn file license Aspose (Total hoặc Words+Cells), tuyệt đối hoặc tương
    /// đối theo ContentRootPath. KHÔNG commit vào repo. Để TRỐNG thì hệ thống tự dò
    /// thư mục quy ước <c>&lt;ContentRoot&gt;/Aspose</c> (tệp <c>*.lic</c> trước, rồi
    /// <c>License Key.txt</c>). Không tìm thấy → Aspose chạy chế độ đánh giá
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

    /// <summary>
    /// Đường dẫn license đã resolve một lần lúc khởi tạo — mọi nhánh Aspose dùng
    /// chung. Ưu tiên <see cref="OfficeAuthoringOptions.AsposeLicensePath"/> (tuyệt
    /// đối, hoặc tương đối theo ContentRootPath); nếu trống thì tự dò thư mục quy
    /// ước <c>&lt;ContentRoot&gt;/Aspose</c> — lấy tệp <c>*.lic</c> đầu tiên, sau đó
    /// tới <c>License Key.txt</c>. Null = không có license → Aspose chạy đánh giá.
    /// </summary>
    private readonly string? _licensePath;

    /// <summary>Test seam: đường dẫn license đã resolve (null = chạy chế độ đánh giá).</summary>
    internal string? ResolvedLicensePath => _licensePath;

    public OfficeAuthoringService(
        UserResourceAccessService resources,
        IOptions<OfficeAuthoringOptions> options,
        Microsoft.Extensions.Hosting.IHostEnvironment environment,
        ILogger<OfficeAuthoringService> logger)
    {
        _resources = resources;
        _options = options.Value;
        _logger = logger;
        _licensePath = ResolveLicensePath(environment.ContentRootPath);
    }

    /// <summary>
    /// Xác định file license theo thứ tự ưu tiên: cấu hình tường minh trước, rồi
    /// mới tới thư mục quy ước <c>&lt;ContentRoot&gt;/Aspose</c>. Trả null khi không
    /// tìm thấy (caller sẽ chạy chế độ đánh giá và cảnh báo trong thông điệp).
    /// </summary>
    private string? ResolveLicensePath(string contentRootPath)
    {
        var configured = _options.AsposeLicensePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var resolved = Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(contentRootPath, configured);
            if (File.Exists(resolved)) return resolved;
            _logger.LogError(
                "📄 [Aspose] OfficeAuthoring:AsposeLicensePath trỏ tới tệp không tồn tại: {Path} — thử thư mục Aspose/.", resolved);
        }

        var asposeDir = Path.Combine(contentRootPath, "Aspose");
        if (!Directory.Exists(asposeDir)) return null;

        // *.lic là định dạng chuẩn; ưu tiên trước "License Key.txt" (thường là bản
        // xuất text của cùng license). Sắp theo tên để chọn tất định khi có nhiều tệp.
        var licFile = Directory.EnumerateFiles(asposeDir, "*.lic")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (licFile is not null) return licFile;

        var keyTxt = Path.Combine(asposeDir, "License Key.txt");
        return File.Exists(keyTxt) ? keyTxt : null;
    }

    public bool IsEnabled => _options.Enabled;

    private bool UseAspose => string.Equals(_options.Engine, AsposeEngineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>create_pdf chỉ tồn tại trên engine Aspose (OpenXml không render PDF).</summary>
    public bool IsPdfAvailable => IsEnabled && UseAspose;

    /// <summary>Họ tool edit/read/convert cần Aspose — engine OpenXml không có chúng.</summary>
    public bool IsAsposeEngine => IsEnabled && UseAspose;

    /// <summary>File dẫn xuất do edit/convert tạo — dispatcher persist vào kho + [FILE:].</summary>
    public sealed record DerivedFile(byte[] Data, string FileName, string ContentType);

    private const int ReadTextMaxChars = 8_000;
    private const int ReadTableDefaultRows = 100;
    private const int MaxEditOperations = 20;

    // ── create_docx ─────────────────────────────────────────────────────

    public (string Message, ProducedFile? File) CreateDocx(Guid tenantId, Guid userId, string input)
    {
        if (!IsEnabled) return ("[Tài liệu] Tool soạn file Office đang tắt (OfficeAuthoring:Enabled).", null);

        var (spec, error) = ParseDocxSpec(input);
        if (error is not null) return (error, null);

        var safeName = SanitizeFileName(spec!.FileName, ".docx", "tai-lieu.docx");
        var (storedName, path) = ReserveStoredFile(tenantId, userId, ".docx", safeName);

        if (UseAspose)
        {
            var licensed = AsposeLicensing.EnsureApplied(_licensePath, _logger);
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
        var (storedName, path) = ReserveStoredFile(tenantId, userId, ".pdf", safeName);
        var licensed = AsposeLicensing.EnsureApplied(_licensePath, _logger);

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
        var (storedName, path) = ReserveStoredFile(tenantId, userId, ".xlsx", safeName);

        if (UseAspose)
        {
            var licensed = AsposeLicensing.EnsureApplied(_licensePath, _logger);
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

    // ── edit / read / convert (Aspose-only; nguồn = bytes đã qua resolver sở hữu) ──

    public (string Message, DerivedFile? File) EditDocxFromBytes(byte[] source, JsonElement payload)
    {
        if (AsposeGateError() is { } gate) return (gate, null);

        var spec = ParseDocxEditSpec(payload, out var parseError);
        if (parseError is not null) return (parseError, null);
        if (!spec!.HasAnyOperation)
            return ("[Tài liệu] Không có phép sửa nào — cần replacements/appendMarkdown/header/footer/pageNumbers.", null);

        var licensed = AsposeLicensing.EnsureApplied(_licensePath, _logger);
        try
        {
            var data = AsposeOfficeEngine.EditDocx(source, spec);
            var name = SanitizeFileName(spec.FileName, ".docx", "da-sua.docx");
            var operationCount = spec.Replacements.Count
                + (string.IsNullOrWhiteSpace(spec.AppendMarkdown) ? 0 : 1)
                + (spec.Header is not null || spec.Footer is not null || spec.PageNumbers is not null ? 1 : 0);
            return (EditedMessage("Word", name, data.Length, operationCount, licensed), new DerivedFile(data, name, DocxContentType));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "📄 [OfficeAuthoring] edit_docx thất bại.");
            return ("[Tài liệu] Không sửa được file Word trên môi trường máy chủ hiện tại — file gốc vẫn nguyên vẹn.", null);
        }
    }

    public (string Message, DerivedFile? File) EditXlsxFromBytes(byte[] source, JsonElement payload)
    {
        if (AsposeGateError() is { } gate) return (gate, null);

        var spec = ParseXlsxEditSpec(payload, out var parseError);
        if (parseError is not null) return (parseError, null);
        if (spec!.Operations.Count == 0)
            return ("[Tài liệu] Không có phép sửa nào trong \"operations\".", null);

        var licensed = AsposeLicensing.EnsureApplied(_licensePath, _logger);
        try
        {
            var data = AsposeOfficeEngine.EditXlsx(source, spec);
            var name = SanitizeFileName(spec.FileName, ".xlsx", "da-sua.xlsx");
            return (EditedMessage("Excel", name, data.Length, spec.Operations.Count, licensed), new DerivedFile(data, name, XlsxContentType));
        }
        catch (InvalidOperationException ex)
        {
            // Lỗi NGHIỆP VỤ từ engine (sheet không tồn tại, op lạ…) — nói thẳng cho agent sửa.
            return ($"[Tài liệu] {ex.Message} File gốc vẫn nguyên vẹn.", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "📊 [OfficeAuthoring] edit_xlsx thất bại.");
            return ("[Tài liệu] Không sửa được file Excel trên môi trường máy chủ hiện tại — file gốc vẫn nguyên vẹn.", null);
        }
    }

    public string ReadDocxFromBytes(byte[] source)
    {
        if (AsposeGateError() is { } gate) return gate;
        AsposeLicensing.EnsureApplied(_licensePath, _logger);
        try
        {
            var text = AsposeOfficeEngine.ExtractDocxText(source).Trim();
            if (text.Length == 0) return "(Tài liệu không có văn bản.)";
            return text.Length <= ReadTextMaxChars
                ? text
                : text[..ReadTextMaxChars] + $"\n… (đã cắt bớt, tài liệu dài {text.Length} ký tự)";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "📄 [OfficeAuthoring] read_docx thất bại.");
            return "[Tài liệu] Không đọc được file — có thể không phải định dạng Word hợp lệ.";
        }
    }

    public string ReadXlsxFromBytes(byte[] source, JsonElement payload)
    {
        if (AsposeGateError() is { } gate) return gate;
        AsposeLicensing.EnsureApplied(_licensePath, _logger);
        try
        {
            var sheet = GetString(payload, "sheet");
            var maxRows = (int)Math.Clamp(GetDouble(payload, "maxRows") ?? ReadTableDefaultRows, 1, 1000);
            return AsposeOfficeEngine.ExtractXlsxTable(source, sheet, maxRows, ReadTextMaxChars);
        }
        catch (InvalidOperationException ex)
        {
            return $"[Tài liệu] {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "📊 [OfficeAuthoring] read_xlsx thất bại.");
            return "[Tài liệu] Không đọc được file — có thể không phải định dạng Excel hợp lệ.";
        }
    }

    public (string Message, DerivedFile? File) ConvertFromBytes(byte[] source, string sourceName, JsonElement payload)
    {
        if (AsposeGateError() is { } gate) return (gate, null);

        var target = (GetString(payload, "to") ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        var sourceExt = Path.GetExtension(sourceName).TrimStart('.').ToLowerInvariant();
        var licensed = AsposeLicensing.EnsureApplied(_licensePath, _logger);

        // Hai tuyến: Words cho văn bản, Cells cho bảng tính — chọn theo ĐUÔI NGUỒN.
        var wordsSources = new[] { "docx", "doc", "rtf", "html", "htm", "txt", "md" };
        var cellsSources = new[] { "xlsx", "xls", "csv" };

        try
        {
            byte[] data;
            string contentType;
            if (wordsSources.Contains(sourceExt))
            {
                var format = target switch
                {
                    "pdf" => Aspose.Words.SaveFormat.Pdf,
                    "docx" => Aspose.Words.SaveFormat.Docx,
                    "html" => Aspose.Words.SaveFormat.Html,
                    "txt" => Aspose.Words.SaveFormat.Text,
                    "rtf" => Aspose.Words.SaveFormat.Rtf,
                    _ => (Aspose.Words.SaveFormat?)null ?? throw new InvalidOperationException(
                        $"Đích \"{target}\" không hỗ trợ cho văn bản. Chọn: pdf, docx, html, txt, rtf.")
                };
                data = AsposeOfficeEngine.ConvertWithWords(source, format);
                contentType = target switch
                {
                    "pdf" => PdfContentType,
                    "docx" => DocxContentType,
                    "html" => "text/html",
                    "txt" => "text/plain",
                    _ => "application/rtf"
                };
            }
            else if (cellsSources.Contains(sourceExt))
            {
                var format = target switch
                {
                    "pdf" => Aspose.Cells.SaveFormat.Pdf,
                    "xlsx" => Aspose.Cells.SaveFormat.Xlsx,
                    "csv" => Aspose.Cells.SaveFormat.CSV,
                    "html" => Aspose.Cells.SaveFormat.Html,
                    _ => (Aspose.Cells.SaveFormat?)null ?? throw new InvalidOperationException(
                        $"Đích \"{target}\" không hỗ trợ cho bảng tính. Chọn: pdf, xlsx, csv, html.")
                };
                data = AsposeOfficeEngine.ConvertWithCells(source, format);
                contentType = target switch
                {
                    "pdf" => PdfContentType,
                    "xlsx" => XlsxContentType,
                    "csv" => "text/csv",
                    _ => "text/html"
                };
            }
            else
            {
                return ($"[Tài liệu] Không nhận dạng được định dạng nguồn \".{sourceExt}\" — hỗ trợ: docx/doc/rtf/html/txt (văn bản), xlsx/xls/csv (bảng tính).", null);
            }

            var stem = Path.GetFileNameWithoutExtension(sourceName);
            var name = SanitizeFileName(GetString(payload, "fileName") ?? $"{stem}.{target}", "." + target, $"chuyen-doi.{target}");
            var note = licensed ? null : EvaluationNote;
            return ($"[Tài liệu] Đã chuyển \"{sourceName}\" → {target.ToUpperInvariant()} \"{name}\" ({FormatSize(data.Length)}) — tệp đính kèm để tải về.{note}",
                new DerivedFile(data, name, contentType));
        }
        catch (InvalidOperationException ex)
        {
            return ($"[Tài liệu] {ex.Message}", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "📄 [OfficeAuthoring] convert_document → {Target} thất bại.", target);
            return target == "pdf"
                ? ("[Tài liệu] Không xuất được PDF trên môi trường máy chủ hiện tại (thiếu engine render chữ). Thử đích khác (docx/html/txt).", null)
                : ("[Tài liệu] Chuyển đổi thất bại — nguồn có thể hỏng hoặc không đúng định dạng.", null);
        }
    }

    private string? AsposeGateError() => !IsEnabled
        ? "[Tài liệu] Tool soạn file Office đang tắt (OfficeAuthoring:Enabled)."
        : !UseAspose
            ? "[Tài liệu] Tool này cần engine Aspose (OfficeAuthoring:Engine=Aspose)."
            : null;

    private static string EditedMessage(string kind, string name, long size, int operationCount, bool licensed)
        => $"[Tài liệu] Đã sửa file {kind} ({operationCount} phép sửa) → \"{name}\" ({FormatSize(size)}) — file MỚI đính kèm để tải, file gốc giữ nguyên.{(licensed ? "" : EvaluationNote)}";

    private static DocxEditSpec? ParseDocxEditSpec(JsonElement payload, out string? error)
    {
        error = null;
        var replacements = new List<DocxReplacementSpec>();
        if (payload.TryGetProperty("operations", out var opsProp) && opsProp.ValueKind == JsonValueKind.Array)
        {
            // Cho phép dạng operations[] tổng quát: gom các op về spec phẳng.
            string? appendMarkdown = null, header = null, footer = null;
            bool? pageNumbers = null;
            var count = 0;
            foreach (var op in opsProp.EnumerateArray())
            {
                if (++count > MaxEditOperations) { error = $"[Tài liệu] Tối đa {MaxEditOperations} phép sửa mỗi lần gọi."; return null; }
                switch ((GetString(op, "op") ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "replacetext":
                        var find = GetString(op, "find");
                        if (string.IsNullOrEmpty(find) || find.Length < 2)
                        { error = "[Tài liệu] replaceText cần \"find\" tối thiểu 2 ký tự."; return null; }
                        replacements.Add(new DocxReplacementSpec
                        {
                            Find = find,
                            Replace = GetString(op, "replace") ?? string.Empty,
                            MatchCase = GetBool(op, "matchCase") ?? false
                        });
                        break;
                    case "appendmarkdown":
                        appendMarkdown = GetString(op, "markdown");
                        break;
                    case "setheaderfooter":
                        header = GetString(op, "header");
                        footer = GetString(op, "footer");
                        pageNumbers = GetBool(op, "pageNumbers");
                        break;
                    default:
                        error = $"[Tài liệu] Phép sửa docx \"{GetString(op, "op")}\" không hỗ trợ (replaceText/appendMarkdown/setHeaderFooter).";
                        return null;
                }
            }
            return new DocxEditSpec
            {
                FileName = GetString(payload, "fileName"),
                Replacements = replacements,
                AppendMarkdown = appendMarkdown,
                Header = header,
                Footer = footer,
                PageNumbers = pageNumbers
            };
        }

        error = "[Tài liệu] Thiếu \"operations\" — mảng các phép sửa (replaceText/appendMarkdown/setHeaderFooter).";
        return null;
    }

    private XlsxEditSpec? ParseXlsxEditSpec(JsonElement payload, out string? error)
    {
        error = null;
        if (!payload.TryGetProperty("operations", out var opsProp) || opsProp.ValueKind != JsonValueKind.Array)
        {
            error = "[Tài liệu] Thiếu \"operations\" — mảng các phép sửa (setCells/addSheet/renameSheet/deleteSheet/setColumnFormat/addChart).";
            return null;
        }

        var operations = new List<XlsxEditOperationSpec>();
        foreach (var op in opsProp.EnumerateArray())
        {
            if (operations.Count >= MaxEditOperations)
            { error = $"[Tài liệu] Tối đa {MaxEditOperations} phép sửa mỗi lần gọi."; return null; }

            var cells = new List<XlsxCellEditSpec>();
            if (op.TryGetProperty("cells", out var cellsProp) && cellsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var cell in cellsProp.EnumerateArray())
                {
                    cells.Add(new XlsxCellEditSpec
                    {
                        Ref = GetString(cell, "ref") ?? string.Empty,
                        Formula = GetString(cell, "formula"),
                        Value = cell.TryGetProperty("value", out var v) ? v.Clone() : null
                    });
                }
            }

            XlsxSheetSpec? newSheet = null;
            if ((GetString(op, "op") ?? string.Empty).Equals("addSheet", StringComparison.OrdinalIgnoreCase))
            {
                List<string>? headers = null;
                if (op.TryGetProperty("headers", out var headersProp) && headersProp.ValueKind == JsonValueKind.Array)
                    headers = headersProp.EnumerateArray().Select(RenderCellText).ToList();
                var rows = new List<List<JsonElement>>();
                if (op.TryGetProperty("rows", out var rowsProp) && rowsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rowsProp.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Array) continue;
                        if (rows.Count >= _options.MaxRowsPerWorkbook)
                        { error = $"[Tài liệu] addSheet vượt trần {_options.MaxRowsPerWorkbook} dòng."; return null; }
                        rows.Add(row.EnumerateArray().Take(_options.MaxColumns).Select(c => c.Clone()).ToList());
                    }
                }
                newSheet = new XlsxSheetSpec
                {
                    Name = GetString(op, "name") ?? "Trang mới",
                    Headers = headers,
                    Rows = rows,
                    FreezeHeader = GetBool(op, "freezeHeader") ?? true
                };
            }

            XlsxChartSpec? chart = null;
            if (op.TryGetProperty("chart", out var chartProp) && chartProp.ValueKind == JsonValueKind.Object)
            {
                var seriesColumns = new List<int>();
                if (chartProp.TryGetProperty("seriesColumns", out var seriesProp) && seriesProp.ValueKind == JsonValueKind.Array)
                    seriesColumns = seriesProp.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).Take(8).ToList();
                chart = new XlsxChartSpec
                {
                    Type = GetString(chartProp, "type") ?? "column",
                    Title = GetString(chartProp, "title"),
                    CategoryColumn = (int)(GetDouble(chartProp, "categoryColumn") ?? 0),
                    SeriesColumns = seriesColumns.Count > 0 ? seriesColumns : [1]
                };
            }

            operations.Add(new XlsxEditOperationSpec
            {
                Op = GetString(op, "op") ?? string.Empty,
                Sheet = GetString(op, "sheet"),
                Cells = cells,
                NewSheet = newSheet,
                From = GetString(op, "from"),
                To = GetString(op, "to"),
                Column = GetDouble(op, "column") is { } col ? (int)col : null,
                Format = GetString(op, "format"),
                Chart = chart
            });
        }

        return new XlsxEditSpec { FileName = GetString(payload, "fileName"), Operations = operations };
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

    /// <summary>Đặt chỗ tệp trong kho upload cô lập của người dùng — tên lưu do server sinh,
    /// giữ stem tên người dùng chọn + GUID khó đoán (xem BuildStoredFileName).</summary>
    private (string StoredName, string Path) ReserveStoredFile(Guid tenantId, Guid userId, string extension, string displayName)
    {
        var uploadDir = _resources.GetUploadDirectory(tenantId, userId);
        Directory.CreateDirectory(uploadDir);
        var storedName = UserResourceAccessService.BuildStoredFileName(displayName, extension);
        return (storedName, Path.Combine(uploadDir, storedName));
    }

    private (string Message, ProducedFile? File) Success(
        string displayName, string storedName, string path, string contentType, string kind, string? note)
    {
        var size = new FileInfo(path).Length;
        _logger.LogInformation("📄 [OfficeAuthoring] Đã tạo {Kind} {Name} ({Size} bytes).", kind, displayName, size);
        var file = new ProducedFile(storedName, displayName, contentType, size);
        return ($"[Tài liệu] Đã tạo file {kind} \"{displayName}\" ({FormatSize(size)}, id tệp: {storedName}) — tệp đính kèm trong câu trả lời để người dùng tải về.{note}", file);
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
