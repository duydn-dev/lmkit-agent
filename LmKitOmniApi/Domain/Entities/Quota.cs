using System.ComponentModel.DataAnnotations;

namespace LmKitOmniApi.Domain.Entities;

/// <summary>
/// Gói hạn mức token theo tháng (vd. "Gói 1K", "Gói Demo 100"). Một gói chỉ là một con số
/// hạn mức có tên; mọi thứ khác (đơn vị nào dùng gói nào, còn bao nhiêu) nằm ở
/// <see cref="Subscription"/> và <see cref="TokenGrant"/> — nhờ vậy gói không bao giờ bị
/// sửa lịch sử của các kỳ đã chạy, chỉ cần ngừng dùng bằng <see cref="IsActive"/>.
/// </summary>
public class Plan
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tên gói hiển thị cho quản trị (duy nhất, không phân biệt hoa/thường).</summary>
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Hạn mức token mỗi kỳ. 0 = không giới hạn (chỉ dùng cho gói nội bộ).</summary>
    public int MonthlyTokenLimit { get; set; }

    /// <summary>Ngừng dùng thì đặt false — gói cũ vẫn tra cứu được cho các kỳ trước.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Gán MỘT gói cho MỘT đơn vị (tenant). Giữ đúng một bản ghi đang hoạt động cho mỗi tenant
/// (unique index TenantId lọc theo IsActive ở tầng truy vấn, xem HermesDbContext) nên không
/// bao giờ có hai nguồn hạn mức tranh nhau trong cùng một kỳ.
/// </summary>
public class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid PlanId { get; set; }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Mốc gia hạn/đổi gói kế tiếp. Null = không đặt kỳ hạn (chạy tới khi đổi).</summary>
    public DateTime? RenewalAtUtc { get; set; }

    /// <summary>False = gói đã bị gỡ khỏi đơn vị (đơn vị quay về không hạn mức).</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Ai gán gói — chỉ để truy vết, null khi seed/di trú dữ liệu.</summary>
    public Guid? CreatedByUserId { get; set; }

    public Tenant? Tenant { get; set; }

    public Plan? Plan { get; set; }
}

/// <summary>
/// Cấp thêm token có thời hạn cho một đơn vị — dùng khi đơn vị cần vượt hạn mức gói trong
/// một khoảng thời gian. Phần đã tiêu được tách riêng (<see cref="UsedTokens"/>) và bị TIÊU
/// HỤT dần theo thời gian, khác hẳn hạn mức gói (reset mỗi kỳ), nên hai nguồn không thể gộp
/// thành một cột.
/// </summary>
public class TokenGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    /// <summary>Tổng token được cấp thêm trong grant này.</summary>
    public int Tokens { get; set; }

    /// <summary>Phần đã tiêu của grant.</summary>
    public int UsedTokens { get; set; }

    /// <summary>Hết hạn thì phần còn lại không còn được tính vào khả dụng. Null = vô hạn.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    [MaxLength(300)]
    public string? Reason { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Guid? CreatedByUserId { get; set; }

    public Tenant? Tenant { get; set; }
}

/// <summary>
/// Số dư token mua trước của một đơn vị (khác hạn mức gói: đây là tài sản, không reset).
/// Một dòng cho mỗi tenant để việc cộng/trừ là một UPDATE nguyên tử thay vì phải cộng
/// nhiều dòng lịch sử mỗi lần đọc dashboard.
/// </summary>
public class TenantCredit
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public int Balance { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
}
