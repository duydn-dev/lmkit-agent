namespace LmKitOmniApi.Application.McpServers;

public sealed record McpServerCatalogEntry(string Name, string BaseUrl, string Description);

/// <summary>
/// Curated, static list of well-known PUBLIC MCP servers surfaced by
/// <c>GET /api/mcp-servers/catalog</c>.
///
/// LƯU Ý QUAN TRỌNG: đây chỉ là các GỢI Ý tham khảo. Việc kết nối không diễn ra tự
/// động — tenant admin vẫn phải tự thêm từng server qua flow hiện có
/// (<c>POST /api/mcp-servers</c>), nơi URL được kiểm tra bởi sandbox SSRF/DNS,
/// header được mã hóa và tool phải qua trust-policy trước khi được dùng. Danh sách
/// này không thực hiện bất kỳ network call nào và không bảo chứng cho nội dung/độ
/// an toàn của các server bên thứ ba.
/// </summary>
public static class McpServerCatalog
{
    public static IReadOnlyList<McpServerCatalogEntry> Entries { get; } =
    [
        new McpServerCatalogEntry(
            "context7",
            "https://mcp.context7.com/mcp",
            "Tài liệu và ví dụ code mới nhất cho hàng nghìn thư viện/framework — hữu ích khi agent cần API reference chính xác theo phiên bản."),
        new McpServerCatalogEntry(
            "deepwiki",
            "https://mcp.deepwiki.com/mcp",
            "Tra cứu và hỏi đáp về các repository GitHub công khai đã được DeepWiki lập chỉ mục (kiến trúc, cách dùng, tài liệu tự sinh)."),
        new McpServerCatalogEntry(
            "microsoft-learn",
            "https://learn.microsoft.com/api/mcp",
            "Tìm kiếm tài liệu chính thức của Microsoft Learn (.NET, Azure, Windows...) trực tiếp từ agent."),
        new McpServerCatalogEntry(
            "hugging-face",
            "https://huggingface.co/mcp",
            "Truy cập hệ sinh thái Hugging Face: tìm model, dataset, paper và Space ngay trong hội thoại."),
        new McpServerCatalogEntry(
            "cloudflare-docs",
            "https://docs.mcp.cloudflare.com/mcp",
            "Tra cứu tài liệu Cloudflare (Workers, R2, DNS, bảo mật...) qua MCP server công khai của Cloudflare.")
    ];
}
