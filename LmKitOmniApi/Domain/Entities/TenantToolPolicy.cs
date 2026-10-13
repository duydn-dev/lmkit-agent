using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

/// <summary>
/// Cấu hình chính sách tool của MỘT đơn vị: siết chặt hơn mức mặc định của hệ thống cho một tool cụ
/// thể (ví dụ chặn hẳn <c>BrowseWeb</c>, hoặc bắt duyệt thêm cả <c>DbQuery</c>).
///
/// <para>
/// <b>Chỉ siết, không nới.</b> Bản ghi này KHÔNG thể làm một tool nhạy cảm chạy tự động — thứ tự
/// nghiêm ngặt được cưỡng chế ở hai lớp: <c>ToolPolicyMutation</c> từ chối lưu một giá trị lỏng hơn
/// mức mặc định, và <c>ToolRiskPolicy</c> khi đọc vẫn lấy giá trị NGHIÊM hơn giữa cấu hình và mức
/// mặc định (nên một dòng lọt vào DB bằng đường khác — restore, sửa tay — cũng không nới được gì).
/// </para>
///
/// <para>
/// <b>Không lưu "mức mặc định" ở đây.</b> Mức nền thuộc về mã nguồn
/// (<c>ToolRiskCatalog</c>), vì nó là một phán đoán về sản phẩm chứ không phải dữ liệu của khách.
/// Bảng này chỉ ghi phần KHÁC đi của một đơn vị, nên khi phân loại nền thay đổi thì các đơn vị chưa
/// từng cấu hình tự động đi theo, không bị đóng băng ở giá trị cũ.
/// </para>
/// </summary>
[Table("tenant_tool_policies")]
public sealed class TenantToolPolicy
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    /// <summary>Tên tool đã được kiểm tra là có trong bảng phân loại lúc ghi.</summary>
    [MaxLength(64)]
    public string ToolName { get; set; } = string.Empty;

    /// <summary>Một trong các giá trị của <c>ToolPolicyEnforcement</c> (Auto / Approval / Blocked).</summary>
    [MaxLength(16)]
    public string Enforcement { get; set; } = string.Empty;

    /// <summary>Lý do vận hành ghi lại — thay đổi chính sách an toàn phải đọc được vì sao.</summary>
    [MaxLength(512)]
    public string? Reason { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
}
