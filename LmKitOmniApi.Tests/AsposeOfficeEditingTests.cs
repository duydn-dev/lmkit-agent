using System.Text.Json;
using LmKitOmniApi.Infrastructure.AI.Documents;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AC = Aspose.Cells;
using AW = Aspose.Words;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Họ tool edit/read/convert trên engine Aspose: sửa docx (thay chữ/nối
/// markdown/header-footer) và xlsx (setCells/công thức/addSheet/rename/format)
/// luôn ra FILE MỚI — bytes gốc không đổi; đọc trả text/bảng có trần; convert
/// đổi định dạng đúng tuyến Words/Cells; engine OpenXml → thông điệp hướng dẫn.
/// Khẳng định sâu chạy trên Windows (CI ubuntu tự thoát sớm — nơi các đường
/// Aspose cần GDI/Skia không đảm bảo); test lỗi nghiệp vụ chạy mọi nền tảng.
/// </summary>
public sealed class AsposeOfficeEditingTests
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

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static byte[] SampleDocx()
    {
        using var stream = new MemoryStream();
        var document = new AW.Document();
        var builder = new AW.DocumentBuilder(document);
        builder.Writeln("Báo cáo DRAFT về trạm quan trắc.");
        builder.Writeln("Số liệu tháng 8.");
        document.Save(stream, AW.SaveFormat.Docx);
        return stream.ToArray();
    }

    private static byte[] SampleXlsx()
    {
        using var stream = new MemoryStream();
        using var workbook = new AC.Workbook();
        workbook.Worksheets.Clear();
        var sheet = workbook.Worksheets.Add("Q3");
        sheet.Cells["A1"].PutValue("Chỉ tiêu");
        sheet.Cells["B1"].PutValue("Giá trị");
        sheet.Cells["A2"].PutValue("pH");
        sheet.Cells["B2"].PutValue(7.0);
        sheet.Cells["A3"].PutValue("COD");
        sheet.Cells["B3"].PutValue(15.0);
        workbook.Save(stream, AC.SaveFormat.Xlsx);
        return stream.ToArray();
    }

    // ── Cross-platform: gate + lỗi nghiệp vụ ────────────────────────────

    [Fact]
    public void EditTools_WithOpenXmlEngine_ReturnGuidance()
    {
        var (service, _) = Build(o => o.Engine = "OpenXml");
        var (message, file) = service.EditDocxFromBytes([1], Payload("""{"operations":[]}"""));
        Assert.Null(file);
        Assert.Contains("Engine=Aspose", message);
        Assert.Contains("Engine=Aspose", service.ReadDocxFromBytes([1]));
    }

    [Fact]
    public void EditDocx_WithNoOperations_OrUnknownOp_RefusesClearly()
    {
        if (!OperatingSystem.IsWindows()) return; // gate qua rồi mới tới parse — giữ đồng nhất với nhánh Aspose
        var (service, _) = Build();
        var (message, file) = service.EditDocxFromBytes(SampleDocx(), Payload("""{"operations":[]}"""));
        Assert.Null(file);
        Assert.Contains("Không có phép sửa", message);

        (message, file) = service.EditDocxFromBytes(SampleDocx(), Payload("""{"operations":[{"op":"deletePage"}]}"""));
        Assert.Null(file);
        Assert.Contains("không hỗ trợ", message);

        // find quá ngắn — chặn thay-chữ-một-ký-tự phá nát tài liệu.
        (message, file) = service.EditDocxFromBytes(SampleDocx(), Payload("""{"operations":[{"op":"replaceText","find":"a"}]}"""));
        Assert.Null(file);
        Assert.Contains("tối thiểu 2 ký tự", message);
    }

    // ── Windows: khẳng định sâu ─────────────────────────────────────────

    [Fact]
    public void EditDocx_ReplacesText_AppendsMarkdown_AndKeepsTheOriginalIntact()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (service, _) = Build();
        var source = SampleDocx();
        var sourceCopy = source.ToArray();

        var (message, derived) = service.EditDocxFromBytes(source, Payload("""
            {"fileName":"ban-chinh-thuc.docx","operations":[
              {"op":"replaceText","find":"DRAFT","replace":"CHÍNH THỨC"},
              {"op":"appendMarkdown","markdown":"## Kết luận\n- Đạt chuẩn"},
              {"op":"setHeaderFooter","header":"TRUNG TÂM CILA","pageNumbers":true}
            ]}
            """));

        Assert.NotNull(derived);
        Assert.Equal("ban-chinh-thuc.docx", derived!.FileName);
        Assert.Contains("Đã sửa file Word (3 phép sửa)", message);
        Assert.Equal(source, sourceCopy); // bytes gốc không bị đụng

        using var stream = new MemoryStream(derived.Data);
        var edited = new AW.Document(stream);
        var text = edited.GetText();
        Assert.Contains("CHÍNH THỨC", text);
        Assert.DoesNotContain("DRAFT", text);
        Assert.Contains("Kết luận", text);
        Assert.Contains("TRUNG TÂM CILA", text);
        Assert.Contains(edited.Range.Fields.Cast<AW.Fields.Field>(), f => f.Type == AW.Fields.FieldType.FieldPage);
    }

    [Fact]
    public void EditXlsx_SetCellsFormula_AddRenameSheet_AndColumnFormat()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (service, _) = Build();
        var (message, derived) = service.EditXlsxFromBytes(SampleXlsx(), Payload("""
            {"operations":[
              {"op":"setCells","sheet":"Q3","cells":[{"ref":"B2","value":7.5},{"ref":"A4","value":"Tổng"},{"ref":"B4","formula":"=SUM(B2:B3)"}]},
              {"op":"setColumnFormat","sheet":"Q3","column":1,"format":"#,##0.00"},
              {"op":"addSheet","name":"Q4","headers":["Chỉ tiêu","Giá trị"],"rows":[["pH",7.1]]},
              {"op":"renameSheet","from":"Q3","to":"Quý 3"}
            ]}
            """));

        Assert.NotNull(derived);
        Assert.Contains("Đã sửa file Excel (4 phép sửa)", message);

        using var stream = new MemoryStream(derived!.Data);
        using var workbook = new AC.Workbook(stream);
        var quy3 = workbook.Worksheets.Cast<AC.Worksheet>().Single(ws => ws.Name == "Quý 3");
        Assert.Equal(7.5, quy3.Cells["B2"].DoubleValue, 3);
        Assert.Equal("=SUM(B2:B3)", quy3.Cells["B4"].Formula);
        Assert.Equal(22.5, quy3.Cells["B4"].DoubleValue, 3); // công thức đã tính
        Assert.Equal("#,##0.00", quy3.Cells["B2"].GetStyle().Custom);
        var quy4 = workbook.Worksheets.Cast<AC.Worksheet>().Single(ws => ws.Name == "Q4");
        Assert.Equal(7.1, quy4.Cells["B2"].DoubleValue, 3);
    }

    [Fact]
    public void EditXlsx_UnknownSheet_NamesTheAvailableOnes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (service, _) = Build();
        var (message, derived) = service.EditXlsxFromBytes(SampleXlsx(), Payload(
            """{"operations":[{"op":"setCells","sheet":"KhongCo","cells":[{"ref":"A1","value":1}]}]}"""));
        Assert.Null(derived);
        Assert.Contains("Q3", message); // liệt kê sheet hiện có để agent tự sửa
    }

    [Fact]
    public void ReadDocx_And_ReadXlsx_ReturnCappedContent()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (service, _) = Build();

        var text = service.ReadDocxFromBytes(SampleDocx());
        Assert.Contains("Báo cáo DRAFT", text);

        var table = service.ReadXlsxFromBytes(SampleXlsx(), Payload("""{"sheet":"Q3","maxRows":2}"""));
        Assert.Contains("Chỉ tiêu", table);
        Assert.Contains("còn 1 dòng", table); // 3 dòng dữ liệu, maxRows=2 → báo còn lại
    }

    [Fact]
    public void ConvertDocument_DocxToTxt_AndXlsxToCsv()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (service, _) = Build();

        var (txtMessage, txt) = service.ConvertFromBytes(SampleDocx(), "nguon.docx", Payload("""{"to":"txt"}"""));
        Assert.NotNull(txt);
        Assert.Equal("text/plain", txt!.ContentType);
        Assert.Contains("DRAFT", System.Text.Encoding.UTF8.GetString(txt.Data));
        Assert.Contains("→ TXT", txtMessage);

        var (_, csv) = service.ConvertFromBytes(SampleXlsx(), "nguon.xlsx", Payload("""{"to":"csv"}"""));
        Assert.NotNull(csv);
        Assert.Contains("pH", System.Text.Encoding.UTF8.GetString(csv!.Data));

        var (badMessage, bad) = service.ConvertFromBytes(SampleDocx(), "nguon.docx", Payload("""{"to":"exe"}"""));
        Assert.Null(bad);
        Assert.Contains("không hỗ trợ", badMessage);
    }
}
