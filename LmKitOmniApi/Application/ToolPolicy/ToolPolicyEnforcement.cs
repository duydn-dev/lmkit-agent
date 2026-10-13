using LmKitOmniApi.Domain.Constants;

namespace LmKitOmniApi.Application.ToolPolicy;

/// <summary>
/// Cách một tool được thực thi: tự chạy, phải có phiếu duyệt, hay chặn hẳn.
///
/// <para>
/// Ba giá trị này là một THỨ TỰ NGHIÊM NGẶT (Auto &lt; Approval &lt; Blocked), không phải ba nhãn
/// rời nhau: mọi quy tắc trong file này đều là so sánh thứ tự — cấu hình đơn vị chỉ được phép đi
/// LÊN trong thứ tự đó.
/// </para>
/// </summary>
public static class ToolPolicyEnforcement
{
    /// <summary>Chạy ngay, không hỏi.</summary>
    public const string Auto = "Auto";

    /// <summary>Phải có một phiếu duyệt của người dùng trước khi chạy.</summary>
    public const string Approval = "Approval";

    /// <summary>Không được chạy, kể cả khi có người muốn duyệt — quyết định ở tầng chính sách.</summary>
    public const string Blocked = "Blocked";

    public static readonly string[] All = [Auto, Approval, Blocked];

    /// <summary>Không phải giá trị hợp lệ → <c>null</c> (người gọi tự quyết định lỗi hay bỏ qua).</summary>
    public static string? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return All.FirstOrDefault(v => string.Equals(v, value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Thứ tự nghiêm ngặt: số càng lớn càng chặt.</summary>
    public static int Rank(string enforcement) => enforcement switch
    {
        Auto => 0,
        Approval => 1,
        Blocked => 2,
        _ => 2 // Giá trị lạ = chặn: fail-closed, không bao giờ rơi về Auto.
    };

    /// <summary>Giá trị nghiêm hơn trong hai giá trị.</summary>
    public static string Strictest(string left, string right)
        => Rank(left) >= Rank(right) ? Norm(left) : Norm(right);

    /// <summary>Mức thực thi MẶC ĐỊNH của một mức rủi ro.</summary>
    public static string DefaultForTier(string tier) => tier switch
    {
        ToolRiskTiers.Read => Auto,
        ToolRiskTiers.Write => Approval,
        ToolRiskTiers.Sensitive => Approval,
        _ => Approval // Mức rủi ro lạ: bắt duyệt, không tự chạy.
    };

    private static string Norm(string value) => TryParse(value) ?? Blocked;
}

/// <summary>Kết quả một yêu cầu đổi chính sách: hoặc hợp lệ, hoặc bị từ chối kèm lý do đọc được.</summary>
public sealed record ToolPolicyMutationResult(bool IsAllowed, string? RejectionReason, string? Enforcement)
{
    public static ToolPolicyMutationResult Allow(string enforcement) => new(true, null, enforcement);

    public static ToolPolicyMutationResult Reject(string reason) => new(false, reason, null);
}

/// <summary>
/// Quy tắc đổi chính sách tool — logic thuần, tách khỏi HTTP và khỏi DB để kiểm chứng được.
///
/// <para>
/// Luật duy nhất: <b>chỉ siết, không nới</b>. Một đơn vị có thể bắt duyệt thêm một tool đang tự chạy
/// (hoặc chặn hẳn nó), nhưng không thể biến một tool đang cần duyệt thành tự chạy. Lý do không phải
/// hình thức: mức mặc định là phán đoán về sản phẩm đã được thiết kế cho mọi khách, còn cấu hình
/// đơn vị là thao tác vận hành hằng ngày; cho phép nới biến "một admin đơn vị lỡ tay" thành sự cố
/// không thu hồi được (Write đã ghi vào CSDL, MCP đã gửi mail). Ai thật sự cần nới thì sửa bảng
/// phân loại trong mã nguồn — nơi có review và có test.
/// </para>
/// </summary>
public static class ToolPolicyMutation
{
    /// <summary>
    /// Kiểm một yêu cầu đặt mức thực thi cho <paramref name="toolName"/>.
    /// </summary>
    public static ToolPolicyMutationResult Validate(string toolName, string? requested)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return ToolPolicyMutationResult.Reject("Thiếu tên tool.");
        }

        // Chỉ tool đã khai báo trong bảng phân loại mới được cấu hình: tool động (MCP:...) có tên
        // sinh theo từng server nên một dòng cấu hình ở đây sẽ nằm lại vô chủ sau khi server đổi
        // tên tool, và tool không tồn tại thì cấu hình là vô nghĩa.
        if (ToolRiskCatalog.All.All(x => !string.Equals(x.ToolName, toolName, StringComparison.OrdinalIgnoreCase)))
        {
            return ToolPolicyMutationResult.Reject(
                $"Tool '{toolName}' không có trong bảng phân loại rủi ro nên không cấu hình được. "
                + "Mức rủi ro của tool phải được khai báo trong mã nguồn có review, không đặt từ API.");
        }

        var enforcement = ToolPolicyEnforcement.TryParse(requested);
        if (enforcement is null)
        {
            return ToolPolicyMutationResult.Reject(
                $"Mức thực thi không hợp lệ. Chỉ nhận: {string.Join(", ", ToolPolicyEnforcement.All)}.");
        }

        var baseline = ToolRiskDefaults.ForTool(toolName);
        if (ToolPolicyEnforcement.Rank(enforcement) < ToolPolicyEnforcement.Rank(baseline))
        {
            return ToolPolicyMutationResult.Reject(
                $"Không thể nới '{toolName}' từ mức mặc định {baseline} xuống {enforcement}: cấu hình đơn vị "
                + "chỉ được siết chặt hơn. Muốn nới thì sửa bảng phân loại rủi ro trong mã nguồn.");
        }

        return ToolPolicyMutationResult.Allow(enforcement);
    }
}

/// <summary>Mức thực thi mặc định của một tool, lấy từ bảng phân loại rủi ro.</summary>
public static class ToolRiskDefaults
{
    public static string TierFor(string toolName) => ToolRiskCatalog.TierOf(toolName);

    public static string ForTool(string toolName)
        => ToolPolicyEnforcement.DefaultForTier(TierFor(toolName));
}

/// <summary>
/// Chính sách CUỐI CÙNG cho một (đơn vị, tool) sau khi áp cấu hình của đơn vị lên mức mặc định.
/// </summary>
/// <param name="ToolName">Tool đang được hỏi.</param>
/// <param name="Tier">Mức rủi ro nền (<c>ToolRiskTiers</c>).</param>
/// <param name="Enforcement">Mức thực thi sẽ áp dụng.</param>
/// <param name="BaselineEnforcement">Mức thực thi mặc định của hệ thống, trước cấu hình đơn vị.</param>
/// <param name="Source">"tenant-override" khi cấu hình đơn vị quyết định, "default" khi không.</param>
/// <param name="Reason">Lý do vận hành ghi kèm ở cấu hình đơn vị (nếu có).</param>
public sealed record ToolRiskDecision(
    string ToolName,
    string Tier,
    string Enforcement,
    string BaselineEnforcement,
    string Source,
    string? Reason)
{
    public bool RequiresApproval => Enforcement == ToolPolicyEnforcement.Approval;

    public bool IsBlocked => Enforcement == ToolPolicyEnforcement.Blocked;

    /// <summary>Chính sách cuối cùng chỉ từ mức mặc định (không có cấu hình đơn vị).</summary>
    public static ToolRiskDecision Default(string toolName)
    {
        var tier = ToolRiskDefaults.TierFor(toolName);
        var baseline = ToolPolicyEnforcement.DefaultForTier(tier);
        return new ToolRiskDecision(toolName, tier, baseline, baseline, "default", null);
    }

    /// <summary>
    /// Áp một cấu hình đơn vị lên mức mặc định. Lấy giá trị NGHIÊM HƠN, không tin dữ liệu trong DB:
    /// một dòng bị sửa tay hoặc phục hồi từ bản sao cũ không được phép nới quyền.
    /// </summary>
    public static ToolRiskDecision Apply(ToolRiskDecision baseline, string? tenantEnforcement, string? reason)
    {
        var parsed = ToolPolicyEnforcement.TryParse(tenantEnforcement);
        if (parsed is null) return baseline;

        var effective = ToolPolicyEnforcement.Strictest(baseline.BaselineEnforcement, parsed);
        var decidedByTenant = ToolPolicyEnforcement.Rank(parsed) > ToolPolicyEnforcement.Rank(baseline.BaselineEnforcement);
        return baseline with
        {
            Enforcement = effective,
            Source = decidedByTenant ? "tenant-override" : "default",
            Reason = decidedByTenant ? reason : null
        };
    }
}
