using System.Globalization;
using LmKitOmniApi.Application.Dashboard;

namespace LmKitOmniApi.Application.Quotas;

/// <summary>
/// Quyết định thuần (không DB, không thời gian) cho cảnh báo hạn mức: đơn vị này có cần cảnh báo
/// không, cảnh báo loại nào, và nội dung ra sao. Tách khỏi worker để luật chạy được test không
/// cần host, và để ngưỡng 80% chỉ tồn tại ở MỘT chỗ (<see cref="DashboardPeriod.OverThresholdPercent"/>)
/// — lệch ngưỡng giữa màn hình và thông báo là loại lỗi người dùng chỉ phát hiện khi đã muộn.
/// </summary>
public static class QuotaAlertRules
{
    /// <summary>Đã dùng từ 80% nhưng CHƯA vượt hạn mức.</summary>
    public const string ThresholdNotificationType = "quota_threshold";

    /// <summary>Đã dùng từ 100% hạn mức trở lên.</summary>
    public const string ExceededNotificationType = "quota_exceeded";

    /// <summary>Loại thông báo cần bắn, hoặc <c>null</c> khi chưa tới ngưỡng.</summary>
    public static string? Classify(int limit, int used)
    {
        // Gói không giới hạn (limit &lt;= 0) không có ngưỡng để vượt: bắn cảnh báo ở đây là báo
        // động giả và sẽ không bao giờ tự dập được.
        if (limit <= 0) return null;

        var percent = Percent(used, limit);
        if (percent >= 100) return ExceededNotificationType;
        if (percent >= DashboardPeriod.OverThresholdPercent) return ThresholdNotificationType;
        return null;
    }

    /// <summary>Phần trăm đã dùng, làm tròn, có thể &gt; 100 khi vượt hạn mức.</summary>
    public static int Percent(int used, int limit)
        => limit <= 0 ? 0 : (int)Math.Round(used * 100d / limit);

    /// <summary>Tiêu đề ngắn gọn đủ để đọc trong danh sách thông báo mà không cần mở.</summary>
    public static string BuildTitle(string notificationType, int percent)
        => notificationType == ExceededNotificationType
            ? $"Đã vượt hạn mức token tháng ({percent}%)"
            : $"Sắp chạm hạn mức token tháng ({percent}%)";

    public static string BuildBody(
        string notificationType,
        string tenantName,
        string planName,
        int used,
        int limit,
        int percent)
    {
        var usage = $"{used.ToString("N0", CultureInfo.InvariantCulture)} / "
            + $"{limit.ToString("N0", CultureInfo.InvariantCulture)} token ({percent}%)";

        return notificationType == ExceededNotificationType
            ? $"Đơn vị {tenantName} đã vượt hạn mức token tháng này: {usage}, gói \"{planName}\". "
              + "Vào Dashboard vận hành → Hạn mức để cấp thêm hạn mức (grant) hoặc nâng gói; "
              + "hạn mức tháng sẽ tự đặt lại đầu tháng sau."
            : $"Đơn vị {tenantName} đã dùng {usage} hạn mức tháng, gói \"{planName}\". "
              + "Vào Dashboard vận hành → Hạn mức để theo dõi và cấp thêm hạn mức trước khi đơn vị chạm trần.";
    }
}
