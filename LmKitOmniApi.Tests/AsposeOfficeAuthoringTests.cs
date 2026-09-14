using LmKitOmniApi.Infrastructure.AI.Documents;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AC = Aspose.Cells;
using AW = Aspose.Words;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Engine Aspose (Words/Cells 20.10) sau façade OfficeAuthoringService:
/// <list type="bullet">
///   <item>Cross-platform: Engine=Aspose LUÔN trả ra một file docx/xlsx hợp lệ —
///   Aspose chạy được thì dùng Aspose, môi trường thiếu GDI/Skia thì façade tự
///   rơi về OpenXml (CI Linux đi nhánh này; hành vi "người dùng vẫn có file"
///   chính là điều được kiểm chứng).</item>
///   <item>Windows (máy dev/prod Windows): khẳng định sâu các tính năng Aspose —
///   Heading style thật, list thật, header/footer + field PAGE, TOC, ô công
///   thức được tính, freeze panes, chart, định dạng cột, PDF magic bytes.</item>
/// </list>
/// Chạy KHÔNG license (test host không có file .lic) — Aspose ở chế độ đánh giá:
/// mọi assert dùng Contains/lọc watermark ("Evaluation Warning" sheet) thay vì
/// đếm tuyệt đối, và thông điệp phải mang cảnh báo watermark.
/// </summary>
public sealed class AsposeOfficeAuthoringTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private static (OfficeAuthoringService Service, UserResourceAccessService Resources) Build(
        Action<OfficeAuthoringOptions>? configure = null)
    {
        var options = new OfficeAuthoringOptions { Engine = "Aspose" };
        configure?.Invoke(options);
        var resources = new UserResourceAccessService(new ToolSandboxService(NullLogger<ToolSandboxService>.Instance));
        var service = new OfficeAuthoringService(
            resources, Options.Create(options), NullLogger<OfficeAuthoringService>.Instance);
        return (service, resources);
    }

    private static string StoredPath(UserResourceAccessService resources, ProducedFile file)
        => Path.Combine(resources.GetUploadDirectory(TenantId, UserId), file.Id);

    private const string RichDocxInput = """
        {"fileName":"bao-cao.docx","title":"BÁO CÁO QUAN TRẮC",
        "markdown":"# Tổng quan\nSố liệu **vượt ngưỡng** tại *hai* trạm.\n## Chi tiết\n- Trạm A\n- Trạm B\n1. Kiến nghị một\n2. Kiến nghị hai\n|Trạm|pH|\n|---|---|\n|A|7.2|\n|B|6.9|",
        "options":{"header":"TRUNG TÂM CILA","footer":"Lưu hành nội bộ","pageNumbers":true,"toc":true}}
        """;

    // ── Cross-platform: façade luôn ra file ─────────────────────────────

    [Fact]
    public void CreateDocx_WithAsposeEngine_AlwaysYieldsAFile_OnAnyPlatform()
    {
        var (service, resources) = Build();
        var (message, file) = service.CreateDocx(TenantId, UserId, RichDocxInput);

        Assert.NotNull(file); // Aspose hoặc fallback OpenXml — người dùng luôn có file
        Assert.StartsWith("[Tài liệu] Đã tạo file Word", message);
        Assert.True(new FileInfo(StoredPath(resources, file!)).Length > 0);
    }

    [Fact]
    public void CreateXlsx_WithAsposeEngine_AlwaysYieldsAFile_OnAnyPlatform()
    {
        var (service, resources) = Build();
        var (message, file) = service.CreateXlsx(TenantId, UserId,
            """{"fileName":"so-lieu.xlsx","sheets":[{"name":"Q3","headers":["Chỉ tiêu","Giá trị"],"rows":[["pH",7.2],["COD",15]]}]}""");

        Assert.NotNull(file);
        Assert.StartsWith("[Tài liệu] Đã tạo file Excel", message);
        Assert.True(new FileInfo(StoredPath(resources, file!)).Length > 0);
    }

    [Fact]
    public void CreatePdf_WithOpenXmlEngine_ReturnsGuidance_NotAFile()
    {
        var (service, _) = Build(o => o.Engine = "OpenXml");
        var (message, file) = service.CreatePdf(TenantId, UserId, """{"markdown":"# x"}""");
        Assert.Null(file);
        Assert.Contains("create_docx", message);
    }

    // ── Windows-only: khẳng định sâu tính năng Aspose ───────────────────
    // (CI chạy ubuntu — các test dưới tự thoát sớm; prod Windows/dev là nơi
    // đường Aspose thật được chứng minh.)

    [Fact]
    public void Docx_HasRealHeadingStyles_Lists_Table_HeaderFooter_AndPageField()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (service, resources) = Build();
        var (message, file) = service.CreateDocx(TenantId, UserId, RichDocxInput);
        Assert.NotNull(file);
        // Không license trong test → phải cảnh báo watermark (trừ khi máy có license cấu hình).
        Assert.Contains("watermark", message);

        var document = new AW.Document(StoredPath(resources, file!));
        var paragraphs = document.GetChildNodes(AW.NodeType.Paragraph, true).Cast<AW.Paragraph>().ToList();

        // Heading style THẬT — TOC nhặt được.
        Assert.Contains(paragraphs, p =>
            p.ParagraphFormat.StyleIdentifier == AW.StyleIdentifier.Heading1 && p.GetText().Contains("Tổng quan"));
        Assert.Contains(paragraphs, p =>
            p.ParagraphFormat.StyleIdentifier == AW.StyleIdentifier.Heading2 && p.GetText().Contains("Chi tiết"));

        // List THẬT (không phải "• " chữ thường).
        Assert.Contains(paragraphs, p => p.IsListItem && p.GetText().Contains("Trạm A"));
        Assert.Contains(paragraphs, p => p.IsListItem && p.GetText().Contains("Kiến nghị một"));

        // Bảng 3 hàng, hàng đầu đậm.
        var table = document.GetChildNodes(AW.NodeType.Table, true).Cast<AW.Tables.Table>().First();
        Assert.Equal(3, table.Rows.Count);

        // Header/footer + field PAGE/NUMPAGES.
        Assert.Contains("TRUNG TÂM CILA", document.GetText());
        Assert.Contains("Lưu hành nội bộ", document.GetText());
        var fieldTypes = document.Range.Fields.Cast<AW.Fields.Field>().Select(f => f.Type).ToList();
        Assert.Contains(AW.Fields.FieldType.FieldPage, fieldTypes);
        Assert.Contains(AW.Fields.FieldType.FieldNumPages, fieldTypes);
        Assert.Contains(AW.Fields.FieldType.FieldTOC, fieldTypes);
    }

    [Fact]
    public void Xlsx_HasFormulas_FreezePanes_Chart_ColumnFormat_AndBoldHeader()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (service, resources) = Build();
        var input = """
            {"fileName":"so-lieu.xlsx","sheets":[{
              "name":"Q3",
              "headers":["Chỉ tiêu","Giá trị"],
              "rows":[["pH",7.2],["COD",15],["Tổng","=SUM(B2:B3)"]],
              "columnFormats":[null,"#,##0.00"],
              "chart":{"type":"column","title":"Biểu đồ Q3","categoryColumn":0,"seriesColumns":[1]}
            }]}
            """;
        var (message, file) = service.CreateXlsx(TenantId, UserId, input);
        Assert.NotNull(file);
        Assert.StartsWith("[Tài liệu] Đã tạo file Excel", message);

        using var workbook = new AC.Workbook(StoredPath(resources, file!));
        // Chế độ đánh giá chèn sheet "Evaluation Warning" — lọc ra khi khẳng định.
        var sheet = workbook.Worksheets.Cast<AC.Worksheet>()
            .Single(ws => ws.Name == "Q3");

        Assert.True(sheet.Cells["A1"].GetStyle().Font.IsBold);          // header đậm
        Assert.Equal(7.2, sheet.Cells["B2"].DoubleValue, 3);            // ô số thật
        Assert.Equal("=SUM(B2:B3)", sheet.Cells["B4"].Formula);        // công thức thật
        Assert.Equal(22.2, sheet.Cells["B4"].DoubleValue, 3);           // đã CalculateFormula
        Assert.Equal("#,##0.00", sheet.Cells["B2"].GetStyle().Custom);  // định dạng cột
        Assert.Single(sheet.Charts);                                    // biểu đồ cạnh dữ liệu
        Assert.True(sheet.GetFreezedPanes(out var row, out _, out _, out _) && row == 1); // đóng băng hàng 1
    }

    [Fact]
    public void Pdf_OnWindows_ProducesARealPdf()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (service, resources) = Build();
        var (message, file) = service.CreatePdf(TenantId, UserId,
            """{"fileName":"bao-cao.pdf","title":"Báo cáo","markdown":"# Mục 1\nNội dung."}""");

        Assert.NotNull(file);
        Assert.StartsWith("[Tài liệu] Đã tạo file PDF", message);
        Assert.Equal("application/pdf", file!.ContentType);

        // Magic bytes "%PDF" — bằng chứng render thật, không phải docx đổi đuôi.
        var header = new byte[4];
        using var stream = File.OpenRead(StoredPath(resources, file));
        Assert.Equal(4, stream.Read(header, 0, 4));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(header));
    }
}
