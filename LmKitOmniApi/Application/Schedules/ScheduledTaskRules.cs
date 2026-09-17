using LmKitOmniApi.Application.Schedules.Commands;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Schedules;

/// <summary>
/// Shared validation and mapping for the create/update/toggle scheduled-task handlers so all
/// mutations enforce the exact same rules and Vietnamese 400 messages.
/// </summary>
public static class ScheduledTaskRules
{
    public const string CompletionRunMode = "completion";
    public const string AgentRunMode = "agent";

    public const int MaxNameLength = 100;
    public const int MaxWebhookUrlLength = 500;
    public const int MaxPromptLength = 2000;
    public const int MinIntervalMinutes = 15;
    /// <summary>Lịch "once" phải hẹn trong tương lai gần: tối đa 366 ngày tới.</summary>
    public const int MaxOnceLeadDays = 366;
    public const int MaxIntervalMinutes = 10080; // 7 days
    public const int MaxEnabledTasksPerUser = 10;

    public static readonly string EnabledCapMessage =
        $"Mỗi người dùng chỉ được bật tối đa {MaxEnabledTasksPerUser} lịch tự động. Vui lòng tắt hoặc xóa bớt lịch hiện có.";

    /// <summary>Returns the Vietnamese 400 message for an invalid request, or <c>null</c> when valid.</summary>
    public static string? Validate(SaveScheduledTaskCommandBase request)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return "Tên lịch không được để trống.";
        if (name.Length > MaxNameLength)
            return $"Tên lịch không được vượt quá {MaxNameLength} ký tự.";

        var prompt = request.Prompt?.Trim();
        if (string.IsNullOrEmpty(prompt))
            return "Nội dung nhắc lệnh không được để trống.";
        if (prompt.Length > MaxPromptLength)
            return $"Nội dung nhắc lệnh không được vượt quá {MaxPromptLength} ký tự.";

        if (NormalizeRunMode(request.RunMode) is null)
            return "Chế độ chạy không hợp lệ. Chỉ hỗ trợ: completion, agent.";

        var webhook = request.DeliveryWebhookUrl?.Trim();
        if (!string.IsNullOrEmpty(webhook))
        {
            if (webhook.Length > MaxWebhookUrlLength)
                return $"URL webhook không được vượt quá {MaxWebhookUrlLength} ký tự.";
            if (!Uri.TryCreate(webhook, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "URL webhook không hợp lệ (chỉ http/https).";
        }

        switch (NormalizeKind(request.ScheduleKind))
        {
            case ScheduleCalculator.IntervalKind:
                if (request.IntervalMinutes is not (>= MinIntervalMinutes and <= MaxIntervalMinutes))
                    return $"Chu kỳ lặp phải từ {MinIntervalMinutes} đến {MaxIntervalMinutes} phút.";
                break;
            case ScheduleCalculator.DailyKind:
                if (request.TimeOfDayMinutes is not (>= 0 and <= ScheduleCalculator.MaxTimeOfDayMinutes))
                    return $"Thời điểm chạy trong ngày phải từ 0 đến {ScheduleCalculator.MaxTimeOfDayMinutes} phút (theo giờ UTC).";
                break;
            case ScheduleCalculator.WeeklyKind:
                if (request.TimeOfDayMinutes is not (>= 0 and <= ScheduleCalculator.MaxTimeOfDayMinutes))
                    return $"Thời điểm chạy trong ngày phải từ 0 đến {ScheduleCalculator.MaxTimeOfDayMinutes} phút (theo giờ UTC).";
                if (request.DayOfWeek is not (>= 0 and <= 6))
                    return "Thứ trong tuần phải từ 0 (Chủ nhật) đến 6 (Thứ bảy).";
                break;
            case ScheduleCalculator.OnceKind:
                if (request.RunAtUtc is not { } runAt)
                    return "Lịch một lần cần thời điểm chạy (runAtUtc, giờ UTC).";
                if (runAt <= DateTime.UtcNow.AddMinutes(1))
                    return "Thời điểm chạy phải ở tương lai (ít nhất 1 phút nữa, giờ UTC).";
                if (runAt > DateTime.UtcNow.AddDays(MaxOnceLeadDays))
                    return $"Thời điểm chạy tối đa {MaxOnceLeadDays} ngày tới.";
                break;
            default:
                return "Loại lịch không hợp lệ. Chỉ hỗ trợ: interval, daily, weekly, once.";
        }

        return null;
    }

    /// <summary>
    /// Copies a validated request onto the entity, nulls out the fields the chosen kind does not
    /// use, and recomputes <see cref="ScheduledTask.NextRunUtc"/> from <paramref name="nowUtc"/>.
    /// </summary>
    public static void Apply(ScheduledTask task, SaveScheduledTaskCommandBase request, DateTime nowUtc)
    {
        var kind = NormalizeKind(request.ScheduleKind);
        task.Name = request.Name.Trim();
        task.Prompt = request.Prompt.Trim();
        task.RunMode = NormalizeRunMode(request.RunMode)!;
        task.CustomAgentId = request.CustomAgentId;
        task.DeliveryWebhookUrl = string.IsNullOrWhiteSpace(request.DeliveryWebhookUrl)
            ? null
            : request.DeliveryWebhookUrl.Trim();
        task.ScheduleKind = kind;
        task.IntervalMinutes = kind == ScheduleCalculator.IntervalKind ? request.IntervalMinutes : null;
        task.TimeOfDayMinutes = kind is ScheduleCalculator.DailyKind or ScheduleCalculator.WeeklyKind
            ? request.TimeOfDayMinutes
            : null;
        task.DayOfWeek = kind == ScheduleCalculator.WeeklyKind ? request.DayOfWeek : null;
        // "once": thời điểm chạy là chính giá trị người dùng đặt (đã validate ở trên);
        // các kind lặp lại mới cần tính lần kế tiếp.
        // BUG đã đo được: `DateTime.SpecifyKind(value, Utc)` chỉ ĐỔI NHÃN Kind mà KHÔNG
        // đổi giờ — client gửi "12:11+07:00" (= 05:11 UTC) bị lưu thành 12:11 UTC, lịch
        // chờ thêm 7 tiếng mới chạy. `.ToUniversalTime()` mới là phép chuyển đúng: giá trị
        // có offset → quy về đúng mốc UTC; giá trị Kind=Unspecified (client cắt offset)
        // giữ nguyên giờ đồng hồ như người dùng nhập và coi là UTC.
        task.NextRunUtc = kind == ScheduleCalculator.OnceKind
            ? request.RunAtUtc!.Value.ToUniversalTime()
            : ScheduleCalculator.ComputeNextRun(task, nowUtc);
    }

    /// <summary>
    /// Sổ sách SAU MỘT LẦN CHẠY — thuần logic để test không cần worker:
    /// <list type="bullet">
    ///   <item>Kind lặp lại: NextRunUtc = lần kế tiếp; riêng Skipped (model/hàng đợi bận
    ///   tạm thời) thử lại sau ~10 phút nhưng không bao giờ MUỘN hơn nhịp bình thường.</item>
    ///   <item>Kind "once": Skipped → thử lại sau 10 phút (vẫn bật); mọi kết quả khác
    ///   (Succeeded/Failed/AwaitingApproval) → lịch TỰ TẮT — đã bắn phát duy nhất.</item>
    /// </list>
    /// Ném InvalidOperationException cho định nghĩa hỏng (caller quyết định vô hiệu hóa).
    /// </summary>
    public static void AdvanceAfterRun(ScheduledTask task, string status, DateTime nowUtc)
    {
        const string skipped = "Skipped";

        if (string.Equals(task.ScheduleKind, ScheduleCalculator.OnceKind, StringComparison.OrdinalIgnoreCase))
        {
            if (status == skipped)
            {
                task.NextRunUtc = nowUtc.AddMinutes(10);
                return;
            }
            task.Enabled = false;
            return;
        }

        var scheduledNextRun = ScheduleCalculator.ComputeNextRun(task, nowUtc);
        task.NextRunUtc = status == skipped
            ? (nowUtc.AddMinutes(10) < scheduledNextRun ? nowUtc.AddMinutes(10) : scheduledNextRun)
            : scheduledNextRun;
    }

    public static Task<int> CountEnabledAsync(HermesDbContext db, Guid tenantId, Guid userId, CancellationToken ct) =>
        db.ScheduledTasks.CountAsync(
            task => task.TenantId == tenantId && task.UserId == userId && task.Enabled, ct);

    public static ScheduledTaskDto ToDto(ScheduledTask task) => new()
    {
        Id = task.Id,
        Name = task.Name,
        Prompt = task.Prompt,
        RunMode = task.RunMode,
        CustomAgentId = task.CustomAgentId,
        DeliveryWebhookUrl = task.DeliveryWebhookUrl,
        ScheduleKind = task.ScheduleKind,
        IntervalMinutes = task.IntervalMinutes,
        TimeOfDayMinutes = task.TimeOfDayMinutes,
        DayOfWeek = task.DayOfWeek,
        Enabled = task.Enabled,
        NextRunUtc = task.NextRunUtc,
        LastRunUtc = task.LastRunUtc,
        LastStatus = task.LastStatus,
        LastError = task.LastError
    };

    private static string NormalizeKind(string? scheduleKind) =>
        scheduleKind?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>
    /// Kiểm tra THAM CHIẾU của request (phần Validate() thuần shape không xem được):
    /// CustomAgentId phải là agent trong tenant mà chủ lịch dùng được (của mình hoặc
    /// chia sẻ tenant); webhook chỉ nhận khi vận hành đã bật ScheduleWebhooks và URL
    /// qua được allowlist + SSRF (URL lẫn DNS). Trả thông điệp 400 tiếng Việt, null = hợp lệ.
    /// </summary>
    public static async Task<string?> ValidateReferencesAsync(
        HermesDbContext db,
        Infrastructure.AI.Web.ScheduleWebhookOptions webhookOptions,
        Infrastructure.AI.Security.ToolSandboxService sandbox,
        SaveScheduledTaskCommandBase request,
        CancellationToken ct)
    {
        if (request.CustomAgentId is { } agentId)
        {
            var accessible = await db.CustomAgents.AnyAsync(agent => agent.Id == agentId
                && agent.TenantId == request.TenantId
                && (agent.OwnerUserId == request.UserId || agent.IsSharedWithTenant), ct);
            if (!accessible)
                return "Agent persona không tồn tại hoặc bạn không có quyền sử dụng.";
        }

        var webhook = request.DeliveryWebhookUrl?.Trim();
        if (!string.IsNullOrEmpty(webhook))
        {
            if (!webhookOptions.Enabled)
                return "Giao kết quả qua webhook chưa được bật trên máy chủ (ScheduleWebhooks:Enabled).";
            if (Infrastructure.AI.Web.ScheduleWebhookDeliverer.ValidateAllowedHost(webhook, webhookOptions) is { } hostError)
                return hostError;
            var validation = await sandbox.ValidateUrlAsync(webhook, ct);
            if (!validation.IsAllowed)
                return validation.DenialReason ?? "URL webhook bị chặn.";
        }

        return null;
    }

    /// <summary>Trống → "completion" (tương thích client cũ); giá trị lạ → null (400).</summary>
    private static string? NormalizeRunMode(string? runMode)
    {
        var normalized = runMode?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized)) return CompletionRunMode;
        return normalized is CompletionRunMode or AgentRunMode ? normalized : null;
    }
}
