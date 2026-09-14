using DocumentFormat.OpenXml.Packaging;
using LmKitOmniApi.Infrastructure.AI.Documents;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Tool soạn file Office: docx/xlsx tạo ra phải là OpenXML HỢP LỆ (đọc lại được),
/// nội dung markdown thành đúng cấu trúc Word (heading/đậm/bảng), số JSON thành ô
/// SỐ thật trong Excel, tên tệp bị sanitize chống traversal, và trần kích thước
/// từ chối bằng thông điệp agent đọc được — không bao giờ ném exception.
/// </summary>
public sealed class OfficeAuthoringServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private static (OfficeAuthoringService Service, UserResourceAccessService Resources) Build(
        Action<OfficeAuthoringOptions>? configure = null)
    {
        // Bộ test này kiểm chứng ENGINE OPENXML (lớp fallback + caps/sanitize dùng
        // chung). Engine Aspose có bộ test riêng: AsposeOfficeAuthoringTests.
        var options = new OfficeAuthoringOptions { Engine = "OpenXml" };
        configure?.Invoke(options);
        var resources = new UserResourceAccessService(new ToolSandboxService(NullLogger<ToolSandboxService>.Instance));
        var service = new OfficeAuthoringService(
            resources, Options.Create(options), new TestHostEnvironment(), NullLogger<OfficeAuthoringService>.Instance);
        return (service, resources);
    }

    private static string StoredPath(UserResourceAccessService resources, ProducedFile file)
        => Path.Combine(resources.GetUploadDirectory(TenantId, UserId), file.Id);

    [Fact]
    public void CreateDocx_ProducesAValidWordFile_WithHeadingsBoldAndTable()
    {
        var (service, resources) = Build();
        var input = """
            {"fileName":"bao-cao.docx","title":"Báo cáo quan trắc",
             "markdown":"# Tổng quan\nSố liệu **vượt ngưỡng** tại *hai* trạm.\n- Trạm A\n- Trạm B\n|Trạm|pH|\n|---|---|\n|A|7.2|\n|B|6.9|"}
            """.Replace("\n             ", "");

        var (message, file) = service.CreateDocx(TenantId, UserId, input);

        Assert.NotNull(file);
        Assert.StartsWith("[Tài liệu] Đã tạo file Word", message);
        Assert.Equal("bao-cao.docx", file!.Name);
        Assert.EndsWith(".docx", file.Id);
        Assert.Contains("wordprocessingml", file.ContentType);

        var path = StoredPath(resources, file);
        Assert.True(File.Exists(path));
        using var document = WordprocessingDocument.Open(path, false);
        var body = document.MainDocumentPart!.Document.Body!;
        var text = body.InnerText;
        Assert.Contains("Báo cáo quan trắc", text);
        Assert.Contains("Tổng quan", text);
        Assert.Contains("vượt ngưỡng", text);
        // **đậm** phải thành run Bold thật, không còn dấu sao.
        Assert.DoesNotContain("**", text);
        Assert.Contains(body.Descendants<W.Run>(),
            run => run.RunProperties?.Bold is not null && run.InnerText.Contains("vượt ngưỡng"));
        // Bảng markdown thành bảng Word thật với 3 hàng (header + 2 dòng).
        var table = Assert.Single(body.Descendants<W.Table>());
        Assert.Equal(3, table.Descendants<W.TableRow>().Count());
    }

    [Fact]
    public void CreateDocx_BareMarkdown_FallsBackToDefaultFileName()
    {
        var (service, _) = Build();
        var (message, file) = service.CreateDocx(TenantId, UserId, "# Ghi chú\nNội dung.");
        Assert.NotNull(file);
        Assert.Equal("tai-lieu.docx", file!.Name);
        Assert.StartsWith("[Tài liệu]", message);
    }

    [Fact]
    public void CreateDocx_SanitizesHostileFileNames()
    {
        var (service, _) = Build();
        var (_, file) = service.CreateDocx(TenantId, UserId,
            """{"fileName":"..\\..\\evil<>.docx","markdown":"x"}""");
        Assert.NotNull(file);
        Assert.DoesNotContain("..", file!.Name);
        Assert.DoesNotContain("\\", file.Name);
        Assert.DoesNotContain("<", file.Name);
        Assert.EndsWith(".docx", file.Name);
        // Tên LƯU do server sinh (guid) — không chịu ảnh hưởng tên client đặt.
        Assert.Matches("^[0-9a-f]{32}\\.docx$", file.Id);
    }

    [Fact]
    public void CreateDocx_OverContentCap_RefusesWithoutCreatingAFile()
    {
        var (service, _) = Build(o => o.MaxContentChars = 10);
        var (message, file) = service.CreateDocx(TenantId, UserId, new string('a', 100));
        Assert.Null(file);
        Assert.Contains("vượt trần", message);
    }

    [Fact]
    public void CreateXlsx_ProducesAValidWorkbook_WithRealNumericCells()
    {
        var (service, resources) = Build();
        var input = """
            {"fileName":"so-lieu.xlsx","sheets":[
              {"name":"Q3","headers":["Chỉ tiêu","Giá trị"],"rows":[["pH",7.2],["COD",15],["Ghi chú","đạt"]]}
            ]}
            """;

        var (message, file) = service.CreateXlsx(TenantId, UserId, input);

        Assert.NotNull(file);
        Assert.StartsWith("[Tài liệu] Đã tạo file Excel", message);
        Assert.Equal("so-lieu.xlsx", file!.Name);

        var path = StoredPath(resources, file);
        using var spreadsheet = SpreadsheetDocument.Open(path, false);
        var workbookPart = spreadsheet.WorkbookPart!;
        var sheet = Assert.Single(workbookPart.Workbook.Descendants<S.Sheet>());
        Assert.Equal("Q3", sheet.Name!.Value);

        var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!.Value!);
        var rows = worksheetPart.Worksheet.Descendants<S.Row>().ToList();
        Assert.Equal(4, rows.Count); // header + 3 dòng

        // 7.2 phải là ô SỐ thật (DataType Number), không phải chuỗi.
        var numericCells = worksheetPart.Worksheet.Descendants<S.Cell>()
            .Where(cell => cell.DataType?.Value == S.CellValues.Number)
            .Select(cell => cell.CellValue!.Text)
            .ToList();
        Assert.Contains("7.2", numericCells);
        Assert.Contains("15", numericCells);
    }

    [Fact]
    public void CreateXlsx_DuplicateAndHostileSheetNames_AreSanitized()
    {
        var (service, resources) = Build();
        var (_, file) = service.CreateXlsx(TenantId, UserId,
            """{"sheets":[{"name":"Bao[cao]/1","rows":[[1]]},{"name":"Bao[cao]/1","rows":[[2]]}]}""");

        Assert.NotNull(file);
        using var spreadsheet = SpreadsheetDocument.Open(StoredPath(resources, file!), false);
        var names = spreadsheet.WorkbookPart!.Workbook.Descendants<S.Sheet>()
            .Select(sheet => sheet.Name!.Value!).ToList();
        Assert.Equal(2, names.Count);
        Assert.Equal(2, names.Distinct(StringComparer.OrdinalIgnoreCase).Count()); // dedupe
        Assert.All(names, name => Assert.DoesNotContain("[", name));
    }

    [Fact]
    public void CreateXlsx_OverRowCap_Refuses()
    {
        var (service, _) = Build(o => o.MaxRowsPerWorkbook = 2);
        var (message, file) = service.CreateXlsx(TenantId, UserId,
            """{"sheets":[{"name":"A","rows":[[1],[2],[3]]}]}""");
        Assert.Null(file);
        Assert.Contains("trần", message);
    }

    [Fact]
    public void BadJson_ReturnsAgentReadableErrors()
    {
        var (service, _) = Build();
        Assert.Contains("JSON không hợp lệ", service.CreateDocx(TenantId, UserId, "{broken").Message);
        Assert.Contains("Payload phải là JSON", service.CreateXlsx(TenantId, UserId, "làm bảng đi").Message);
        Assert.Contains("sheets", service.CreateXlsx(TenantId, UserId, "{}").Message);
    }

    [Fact]
    public void WhenDisabled_RefusesWithoutTouchingDisk()
    {
        var (service, _) = Build(o => o.Enabled = false);
        var (message, file) = service.CreateDocx(TenantId, UserId, "# x");
        Assert.Null(file);
        Assert.Contains("đang tắt", message);
    }
}
