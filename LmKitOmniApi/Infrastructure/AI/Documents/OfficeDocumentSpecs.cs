using System.Text.Json;

namespace LmKitOmniApi.Infrastructure.AI.Documents;

/// <summary>
/// Spec trung gian cho tài liệu Word/PDF — kết quả parse payload của agent, dùng
/// chung cho CẢ HAI engine (Aspose và OpenXml fallback) để hai bên không bao giờ
/// hiểu payload khác nhau.
/// </summary>
public sealed class DocxSpec
{
    public string? FileName { get; init; }
    public string? Title { get; init; }
    public string Markdown { get; init; } = string.Empty;
    public DocxOptionsSpec Options { get; init; } = new();
}

/// <summary>
/// Tùy chọn trình bày — engine Aspose áp dụng đầy đủ; engine OpenXml (fallback)
/// bỏ qua phần nó không hỗ trợ (header/footer/TOC) và vẫn ra file hợp lệ.
/// Mặc định theo văn bản hành chính VN: A4, Times New Roman 13, giãn dòng 1.5,
/// lề trái 30mm / phải 20mm / trên dưới 20mm.
/// </summary>
public sealed class DocxOptionsSpec
{
    public string FontName { get; init; } = "Times New Roman";
    public double FontSize { get; init; } = 13;
    public double LineSpacing { get; init; } = 1.5;
    public string? Header { get; init; }
    public string? Footer { get; init; }
    public bool PageNumbers { get; init; }
    public bool Toc { get; init; }
}

/// <summary>Spec workbook Excel — parse một lần, hai engine cùng dùng.</summary>
public sealed class XlsxSpec
{
    public string? FileName { get; init; }
    public List<XlsxSheetSpec> Sheets { get; init; } = [];
}

public sealed class XlsxSheetSpec
{
    public string Name { get; init; } = "Trang 1";
    public List<string>? Headers { get; init; }
    public List<List<JsonElement>> Rows { get; init; } = [];

    /// <summary>Đóng băng hàng tiêu đề (chỉ engine Aspose; mặc định bật khi có headers).</summary>
    public bool FreezeHeader { get; init; } = true;

    /// <summary>Độ rộng cột tùy chọn (đơn vị ký tự Excel). Thiếu → tự ước lượng KHÔNG cần đo chữ.</summary>
    public List<double>? ColumnWidths { get; init; }

    /// <summary>Định dạng số theo cột, ví dụ "#,##0.00" hay "dd/mm/yyyy" (chỉ engine Aspose).</summary>
    public List<string?>? ColumnFormats { get; init; }

    /// <summary>Biểu đồ tùy chọn vẽ cạnh vùng dữ liệu (chỉ engine Aspose).</summary>
    public XlsxChartSpec? Chart { get; init; }
}

/// <summary>Một phép thay thế văn bản trong edit_docx.</summary>
public sealed class DocxReplacementSpec
{
    public string Find { get; init; } = string.Empty;
    public string Replace { get; init; } = string.Empty;
    public bool MatchCase { get; init; }
}

/// <summary>
/// Các phép sửa MỘT LẦN GỌI cho edit_docx — declarative, không stateful:
/// thay chữ, nối thêm nội dung markdown vào cuối, đặt header/footer/số trang.
/// File gốc KHÔNG bị đổi — kết quả là file mới trong kho người dùng.
/// </summary>
public sealed class DocxEditSpec
{
    public string? FileName { get; init; }
    public List<DocxReplacementSpec> Replacements { get; init; } = [];
    public string? AppendMarkdown { get; init; }
    public string? Header { get; init; }
    public string? Footer { get; init; }
    public bool? PageNumbers { get; init; }

    public bool HasAnyOperation =>
        Replacements.Count > 0 || !string.IsNullOrWhiteSpace(AppendMarkdown)
        || Header is not null || Footer is not null || PageNumbers is not null;
}

/// <summary>Một phép sửa của edit_xlsx; <see cref="Op"/> quyết định trường nào có nghĩa.</summary>
public sealed class XlsxEditOperationSpec
{
    /// <summary>"setCells" | "addSheet" | "renameSheet" | "deleteSheet" | "setColumnFormat" | "addChart".</summary>
    public string Op { get; init; } = string.Empty;

    public string? Sheet { get; init; }
    public List<XlsxCellEditSpec> Cells { get; init; } = [];      // setCells
    public XlsxSheetSpec? NewSheet { get; init; }                  // addSheet
    public string? From { get; init; }                             // renameSheet
    public string? To { get; init; }                               // renameSheet
    public int? Column { get; init; }                              // setColumnFormat
    public string? Format { get; init; }                           // setColumnFormat
    public XlsxChartSpec? Chart { get; init; }                     // addChart
}

/// <summary>Một ô trong setCells: tham chiếu A1 + giá trị HOẶC công thức.</summary>
public sealed class XlsxCellEditSpec
{
    public string Ref { get; init; } = string.Empty;
    public System.Text.Json.JsonElement? Value { get; init; }
    public string? Formula { get; init; }
}

public sealed class XlsxEditSpec
{
    public string? FileName { get; init; }
    public List<XlsxEditOperationSpec> Operations { get; init; } = [];
}

/// <summary>
/// Biểu đồ khai báo: cột phân loại + các cột giá trị (chỉ số 0-based trong bảng).
/// Đối tượng chart được ghi vào XML của workbook — không render ảnh nên không
/// đụng GDI, an toàn cả trên Linux.
/// </summary>
public sealed class XlsxChartSpec
{
    /// <summary>"column" | "line" | "pie".</summary>
    public string Type { get; init; } = "column";
    public string? Title { get; init; }
    public int CategoryColumn { get; init; }
    public List<int> SeriesColumns { get; init; } = [];
}
