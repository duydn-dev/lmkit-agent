namespace LmKitOmniApi.Domain.Constants;

/// <summary>
/// Mức rủi ro của một tool — trục CHÍNH SÁCH, tách khỏi trục QUYỀN (RBAC theo vai trò).
///
/// <para>
/// Vì sao phải tách: trước đây hai câu hỏi "vai trò này có được gọi tool không" và "tool này có
/// cần người duyệt không" nằm lẫn trong cùng một lớp, dưới dạng ba HashSet viết tay
/// (<c>RoleToolPermissions</c>, <c>ApprovalRequiredTools</c>, <c>ScheduledWriteTools</c>). Hệ quả là
/// không ai trả lời được câu hỏi vận hành rất thật: "tool nào của hệ thống này tự chạy, tool nào
/// chờ người bấm duyệt, và đơn vị tôi đang chặn hẳn cái gì" — và cũng không có chỗ nào để siết chặt
/// thêm cho một đơn vị cụ thể.
/// </para>
/// </summary>
public static class ToolRiskTiers
{
    /// <summary>Chỉ đọc dữ liệu / chỉ tính toán trong bộ nhớ: tự chạy, không cần người duyệt.</summary>
    public const string Read = "Read";

    /// <summary>Có tác dụng phụ (ghi CSDL, tạo lịch, tạo file mới ở ngoài): mặc định phải duyệt.</summary>
    public const string Write = "Write";

    /// <summary>
    /// Tác dụng phụ KHÔNG thể thu hồi hoặc vượt ra ngoài ranh giới hệ thống (xoá dữ liệu, gọi tool
    /// MCP của bên thứ ba). Mặc định phải duyệt, và <b>không đơn vị nào được nới</b> mức này —
    /// xem <c>ToolPolicyEnforcement</c>.
    /// </summary>
    public const string Sensitive = "Sensitive";
}

/// <summary>
/// Bảng phân loại rủi ro của mọi tool hệ thống — NGUỒN SỰ THẬT DUY NHẤT.
///
/// <para>
/// Cả hai đường đọc đều lấy từ đây: <c>ToolPermissionService</c> (quyết định có cần phiếu duyệt
/// không) và <c>ToolRiskPolicy</c> (quyết định cuối cùng sau khi áp cấu hình của đơn vị). Trước đây
/// cùng một sự thật ấy được viết lại ở ba chỗ, nên chỉ cần quên một chỗ là tool đã được duyệt ở
/// đường này lại tự chạy ở đường khác.
/// </para>
///
/// <para>
/// <b>Fail-closed:</b> tool KHÔNG có trong bảng được coi là <see cref="ToolRiskTiers.Sensitive"/>.
/// Một tool lạ đã bị chặn ở tầng quyền theo vai trò (danh sách vai trò không chứa nó), nên nhánh
/// này chỉ có nghĩa khi ai đó thêm tool vào danh sách vai trò mà quên khai báo mức rủi ro — và khi
/// đó "cần người duyệt" là câu trả lời an toàn, không phải "tự chạy".
/// </para>
/// </summary>
public static class ToolRiskCatalog
{
    /// <summary>Một dòng trong bảng phân loại.</summary>
    public sealed record Entry(string ToolName, string Tier, string Why);

    private const string McpPrefix = "MCP:";
    private const string McpTrustedReadPrefix = "MCP:TRUSTED_READ:";

    private static readonly Entry[] Entries =
    [
        // ── Write: tạo/sửa dữ liệu hoặc cấu hình sống lâu dài → người duyệt trước ──
        new("DbWrite", ToolRiskTiers.Write,
            "ghi vào CSDL nghiệp vụ của khách; nhánh có phê duyệt mới sao lưu bảng đích trước khi ghi"),
        new("CallApiWrite", ToolRiskTiers.Write,
            "POST/PUT/PATCH/DELETE ra hệ thống ngoài — payload phải có người vet trước khi bắn"),
        new("BrowseWeb", ToolRiskTiers.Write,
            "mở trình duyệt headless tới URL do người vận hành duyệt — egress có tác dụng phụ"),
        new("ScheduleTask", ToolRiskTiers.Write,
            "tạo lịch chạy tự động sống lâu dài dưới danh tính người dùng"),
        new("IngestDocument", ToolRiskTiers.Write,
            "nạp tài liệu vào kho tri thức dùng chung — thay đổi ngữ cảnh mà agent trả lời"),
        new("DeleteDocument", ToolRiskTiers.Sensitive,
            "xoá dữ liệu: không thu hồi được"),

        // ── Sensitive: không được nới bởi cấu hình đơn vị ──
        // (MCP động được xử lý trong TierOf — xem chú thích ở đó.)

        // ── Read: chỉ đọc / chỉ tính toán cục bộ → tự chạy ──
        new("SearchWeb", ToolRiskTiers.Read, "tìm kiếm chỉ đọc, đã qua egress policy"),
        new("FetchWeb", ToolRiskTiers.Read,
            "GET + trích nội dung chỉ đọc (không dựng container, không chạy script trang)"),
        new("DbQuery", ToolRiskTiers.Read, "SELECT chỉ đọc, phân loại câu lệnh ở tầng SQL"),
        new("CallApi", ToolRiskTiers.Read, "GET/HEAD ra ngoài — không tạo/sửa dữ liệu"),
        new("QueryKnowledgeBase", ToolRiskTiers.Read, "truy vấn kho tri thức"),
        new("AnalyzeText", ToolRiskTiers.Read, "phân tích văn bản cục bộ"),
        new("AnalyzeImage", ToolRiskTiers.Read, "phân tích ảnh cục bộ"),
        new("TranscribeAudio", ToolRiskTiers.Read, "bóc lời thoại cục bộ"),
        new("ReadPdfDocument", ToolRiskTiers.Read, "đọc PDF của chính người gọi"),
        new("ReadWordDocument", ToolRiskTiers.Read, "đọc Word của chính người gọi"),
        new("ReadExcelDocument", ToolRiskTiers.Read, "đọc Excel của chính người gọi"),
        new("ReadPdfForm", ToolRiskTiers.Read, "đọc form PDF của chính người gọi"),
        // Các tool tài liệu dưới đây LUÔN tạo file MỚI trong kho riêng của người gọi, không sửa
        // bản gốc và không ra mạng — cùng nhóm với AuthorDocument, nên tự chạy.
        new("FillPdfForm", ToolRiskTiers.Read, "điền form ra file mới trong kho riêng, bản gốc giữ nguyên"),
        new("RedactPdf", ToolRiskTiers.Read, "che dữ liệu ra file mới trong kho riêng, bản gốc giữ nguyên"),
        new("RedactOffice", ToolRiskTiers.Read, "che dữ liệu Office ra file mới trong kho riêng"),
        new("ValidatePdfA", ToolRiskTiers.Read, "kiểm tra chuẩn PDF/A, không sửa tệp"),
        new("AuthorDocument", ToolRiskTiers.Read, "soạn Word/Excel vào kho riêng của người gọi"),
        new("RunCode", ToolRiskTiers.Read, "chạy JS trong sandbox Jint — không CLR/mạng/tệp"),
        new("RunPython", ToolRiskTiers.Read, "chạy Python trong container cô lập, không mạng"),
        // Delegate không tự làm gì: nó chỉ chuyển việc sang agent phụ, và mọi tool mà agent phụ
        // gọi lại đi qua ĐÚNG cửa kiểm tra này một lần nữa.
        new("Delegate", ToolRiskTiers.Read, "uỷ quyền cho agent phụ; tool của agent phụ vẫn qua cửa này"),
        new("ScheduleManage", ToolRiskTiers.Read, "xem/tắt lịch — chiều an toàn của automation")
    ];

    private static readonly Dictionary<string, Entry> ByName =
        Entries.ToDictionary(e => e.ToolName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Toàn bộ bảng phân loại (dùng cho API chính sách và tài liệu vận hành).</summary>
    public static IReadOnlyList<Entry> All => Entries;

    /// <summary>
    /// Mức rủi ro của một tool. Tool MCP động (<c>MCP:...</c>) là <see cref="ToolRiskTiers.Sensitive"/>
    /// trừ đúng trường hợp server đã được admin đơn vị tin cậy và công bố annotation read-only
    /// (<c>MCP:TRUSTED_READ:...</c>) — annotation của server ngoài là GỢI Ý không đáng tin, nên mặc
    /// định phải duyệt. Tool không khai báo → Sensitive (fail-closed).
    /// </summary>
    public static string TierOf(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return ToolRiskTiers.Sensitive;

        if (toolName.StartsWith(McpTrustedReadPrefix, StringComparison.OrdinalIgnoreCase))
            return ToolRiskTiers.Read;

        if (toolName.StartsWith(McpPrefix, StringComparison.OrdinalIgnoreCase))
            return ToolRiskTiers.Sensitive;

        return ByName.TryGetValue(toolName, out var entry) ? entry.Tier : ToolRiskTiers.Sensitive;
    }

    /// <summary>Lý do phân loại (rỗng với tool không khai báo và với tool MCP động).</summary>
    public static string WhyOf(string toolName)
        => ByName.TryGetValue(toolName, out var entry) ? entry.Why : string.Empty;

    /// <summary>
    /// Tool nào cần phiếu duyệt THEO MẶC ĐỊNH — đúng tập mà hành vi cũ (<c>ApprovalRequiredTools</c>)
    /// thực thi: mọi tool ở mức Write hoặc Sensitive.
    /// </summary>
    public static IReadOnlyList<string> ApprovalRequiredTools =>
        Entries.Where(e => e.Tier is ToolRiskTiers.Write or ToolRiskTiers.Sensitive)
            .Select(e => e.ToolName)
            .ToList();
}
