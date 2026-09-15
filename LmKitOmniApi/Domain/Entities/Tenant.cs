using System.ComponentModel.DataAnnotations;

namespace LmKitOmniApi.Domain.Entities;

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Tên hiển thị của trợ lý AI cho riêng tenant này (vd. "Trợ lý CILA", "AI Sở TN&amp;MT").
    /// Dùng trên header, nhãn tin nhắn trợ lý trong khung chat, và trong system prompt để
    /// agent tự xưng đúng tên. Rỗng/null → dùng mặc định "CILA Agent".
    /// </summary>
    [MaxLength(100)]
    public string? AgentDisplayName { get; set; }

    /// <summary>
    /// Logo của tenant (nội dung file ảnh). Chỉ phục vụ qua endpoint có xác thực và KHÔNG
    /// bao giờ được kéo về trong các truy vấn thường — mọi projection tenant chỉ đọc cờ
    /// "có logo hay không" (<see cref="LogoUpdatedAt"/>) chứ không nạp mảng byte này.
    /// </summary>
    public byte[]? LogoData { get; set; }

    /// <summary>Content-type của <see cref="LogoData"/> (vd. "image/png"), dùng khi trả ảnh.</summary>
    [MaxLength(100)]
    public string? LogoContentType { get; set; }

    /// <summary>Mốc cập nhật logo gần nhất — làm cache-buster cho URL ảnh phía client.</summary>
    public DateTime? LogoUpdatedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Quan hệ 1-N với ChatSessions
    public ICollection<ChatSession> ChatSessions { get; set; } = new List<ChatSession>();
}
