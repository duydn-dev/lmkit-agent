using LmKitOmniApi.Application.ToolPolicy;
using LmKitOmniApi.Domain.Entities;

namespace LmKitOmniApi.Application.Abstractions;

/// <summary>
/// Trả lời câu hỏi "tool này, ở ĐƠN VỊ này, thì tự chạy hay phải duyệt hay bị chặn".
///
/// <para>
/// Tách khỏi <see cref="IToolPermissionService"/> có chủ đích: dịch vụ kia giữ QUYỀN theo vai trò và
/// hạn mức tần suất (thay đổi theo người dùng, theo phút), còn đây giữ CHÍNH SÁCH theo đơn vị (thay
/// đổi rất ít, do admin cấu hình). Gộp hai thứ vào một chỗ là cách chắc chắn nhất để một thay đổi
/// hạn mức vô tình nới quyền duyệt, hoặc ngược lại.
/// </para>
/// </summary>
public interface IToolRiskPolicy
{
    /// <summary>
    /// Chính sách đã áp cho (đơn vị, tool). KHÔNG ném: nếu không đọc được cấu hình (DB lỗi, timeout)
    /// thì trả về mức mặc định nghiêm ngặt theo bảng phân loại rủi ro — cấu hình không đọc được
    /// không bao giờ được biến thành "chạy tự do".
    /// </summary>
    Task<ToolRiskDecision> ResolveAsync(Guid tenantId, string toolName, CancellationToken ct = default);

    /// <summary>Cấu hình đơn vị đang có hiệu lực, khoá theo tên tool (so khớp không phân biệt hoa thường).</summary>
    Task<IReadOnlyDictionary<string, TenantToolPolicy>> ListOverridesAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Quên cấu hình đã cache của một đơn vị (gọi ngay sau khi ghi cấu hình).</summary>
    void Invalidate(Guid tenantId);
}
