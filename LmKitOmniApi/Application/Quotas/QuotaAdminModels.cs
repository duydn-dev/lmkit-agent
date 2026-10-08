namespace LmKitOmniApi.Application.Quotas;

/// <summary>
/// Kết quả chung của mọi thao tác GHI trong khối hạn mức, cùng khuôn với
/// <c>TenantMutationResult</c>: handler trả lỗi NGHIỆP VỤ bằng tiếng Việt, controller chỉ ánh xạ
/// sang mã HTTP. Nhờ vậy luật (gói trùng tên, gói đã ngừng dùng, grant đã tiêu…) nằm một chỗ và
/// test được không cần HTTP.
/// </summary>
public sealed record QuotaMutationResult(bool Success, string? Error, Guid? Id = null, bool IsNotFound = false);

/// <summary>
/// Body tạo/sửa gói. <c>MonthlyTokenLimit = 0</c> nghĩa là KHÔNG GIỚI HẠN (gói nội bộ) — đây là
/// quy ước của <c>Plan.MonthlyTokenLimit</c>, không phải "gói 0 token".
/// </summary>
public sealed record SavePlanRequest(string? Name, int MonthlyTokenLimit, bool? IsActive);

/// <summary>Gán gói cho đơn vị. <c>RenewalAtUtc</c> null = không đặt kỳ hạn.</summary>
public sealed record AssignPlanRequest(Guid PlanId, DateTime? RenewalAtUtc);

/// <summary>Cấp thêm token có thời hạn cho đơn vị. <c>ExpiresAtUtc</c> null = vô hạn.</summary>
public sealed record SaveGrantRequest(int Tokens, DateTime? ExpiresAtUtc, string? Reason);

/// <summary>Đặt lại số dư token mua trước của đơn vị.</summary>
public sealed record SaveCreditRequest(int Balance);

public sealed class PlanDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int MonthlyTokenLimit { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAtUtc { get; init; }

    /// <summary>Số đơn vị ĐANG dùng gói này — chặn "xoá gói còn người dùng" ngay trên UI.</summary>
    public int TenantCount { get; init; }
}

public sealed class GrantDto
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public int Tokens { get; init; }
    public int UsedTokens { get; init; }
    public int RemainingTokens { get; init; }
    public DateTime? ExpiresAtUtc { get; init; }
    public string? Reason { get; init; }
    public DateTime CreatedAtUtc { get; init; }

    /// <summary>Đã quá <see cref="ExpiresAtUtc"/>: phần còn lại không còn tiêu được.</summary>
    public bool IsExpired { get; init; }
}

/// <summary>
/// Trạng thái hạn mức của MỘT đơn vị cho màn quản trị — đủ để hiển thị và đủ để sửa, nên màn
/// hình không phải ghép thêm truy vấn nào để biết "gán gì, sửa gì".
/// </summary>
public sealed class TenantQuotaAdminDto
{
    public Guid TenantId { get; init; }
    public string TenantName { get; init; } = string.Empty;

    public Guid? SubscriptionId { get; init; }
    public Guid? PlanId { get; init; }
    public string? PlanName { get; init; }
    public int MonthlyTokenLimit { get; init; }
    public DateTime? RenewalAtUtc { get; init; }

    /// <summary>Token đã dùng trong tháng dương lịch hiện tại — CÙNG nguồn với dashboard và với
    /// worker cảnh báo (<see cref="TenantQuotaUsage"/>), gồm cả lượt chat lẫn lần chạy agent.</summary>
    public int UsedTokens { get; init; }

    public int UtilizationPct { get; init; }
    public bool IsUnlimited { get; init; }
    public int CreditBalance { get; init; }

    public int ActiveGrantCount { get; init; }
    public int GrantRemainingTokens { get; init; }
}

/// <summary>
/// Luật "grant còn hiệu lực": CHƯA quá hạn (<c>null</c> = vô hạn) và CÒN token. Một chỗ duy
/// nhất vì cả màn quản trị lẫn worker cảnh báo đều phải coi một grant đã tiêu hết hoặc đã hết
/// hạn là KHÔNG còn khả dụng — đếm nó là hứa hạn mức không tồn tại.
/// </summary>
public static class QuotaGrants
{
    public static bool IsActive(DateTime? expiresAtUtc, int tokens, int usedTokens, DateTime now)
        => (expiresAtUtc is null || expiresAtUtc > now) && tokens - usedTokens > 0;
}
