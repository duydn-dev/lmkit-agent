using System.Text;
using System.Text.Json;
using LmKitOmniApi.Application.Schedules;
using LmKitOmniApi.Application.Schedules.Commands;
using LmKitOmniApi.Application.Schedules.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Infrastructure.AI.Schedules;

/// <summary>
/// Đầu tool cho agent tự quản lý lịch theo NGÔN NGỮ TỰ NHIÊN của người dùng:
/// <list type="bullet">
///   <item><c>schedule_task</c> (SCHEDULE_CREATE — nằm trong ApprovalRequiredTools: tạo
///   automation sống lâu dài là hành động quan trọng, card phê duyệt hiện đúng
///   tên/prompt/chu kỳ trước khi chạy): parse JSON của agent rồi đi qua ĐÚNG
///   CreateScheduledTaskCommand — cùng validation, cùng cap 10 lịch bật/user,
///   cùng kiểm tra persona/webhook như tạo từ màn Task Scheduler.</item>
///   <item><c>list_schedules</c> (SCHEDULE_LIST): danh sách gọn để agent trả lời
///   "tôi đang có lịch nào" và lấy id cho lệnh hủy.</item>
///   <item><c>cancel_schedule</c> (SCHEDULE_CANCEL): TẮT lịch (Enabled=false — thuận
///   chiều an toàn, đảo ngược được từ màn /schedules; tool không bao giờ xóa hẳn).</item>
/// </list>
/// Mọi lỗi trả về chuỗi "[Lịch] …" agent đọc được; không bao giờ ném ra ngoài.
/// </summary>
public sealed class ScheduleToolService
{
    private const int ListPageSize = 20;

    private readonly IMediator _mediator;
    private readonly HermesDbContext _dbContext;
    private readonly ILogger<ScheduleToolService> _logger;

    public ScheduleToolService(IMediator mediator, HermesDbContext dbContext, ILogger<ScheduleToolService> logger)
    {
        _mediator = mediator;
        _dbContext = dbContext;
        _logger = logger;
    }

    // ── schedule_task ───────────────────────────────────────────────────

    public async Task<string> CreateAsync(Guid tenantId, Guid userId, string input, CancellationToken ct)
    {
        var (request, error) = ParseCreateInput(input);
        if (error is not null) return $"[Lịch] {error}";

        var result = await _mediator.Send(new CreateScheduledTaskCommand
        {
            TenantId = tenantId,
            UserId = userId,
            Name = request!.Name,
            Prompt = request.Prompt,
            RunMode = request.RunMode,
            CustomAgentId = request.CustomAgentId,
            DeliveryWebhookUrl = request.DeliveryWebhookUrl,
            ScheduleKind = request.Kind,
            IntervalMinutes = request.IntervalMinutes,
            TimeOfDayMinutes = request.TimeOfDayMinutes,
            DayOfWeek = request.DayOfWeek,
            RunAtUtc = request.RunAtUtc
        }, ct);

        if (result.Status != ScheduledTaskMutationStatus.Success)
            return $"[Lịch] Không tạo được: {result.ErrorMessage}";

        var task = result.Task!;
        _logger.LogInformation("📅 [ScheduleTool] Đã tạo lịch {TaskId} ({Name}) cho user {UserId}.", task.Id, task.Name, userId);
        return $"[Lịch] Đã tạo lịch \"{task.Name}\" (id {ShortId(task.Id)}) — {DescribeCadence(task)}, chế độ {task.RunMode}. " +
               "Lần chạy kế tiếp (UTC): " + task.NextRunUtc.ToString("yyyy-MM-dd HH:mm") + ". " +
               "Người dùng xem/sửa/tắt tại màn Task Scheduler (/schedules).";
    }

    // ── list_schedules ──────────────────────────────────────────────────

    public async Task<string> ListAsync(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var page = await _mediator.Send(new ListScheduledTasksQuery
        {
            TenantId = tenantId,
            UserId = userId,
            Page = 1,
            PageSize = ListPageSize
        }, ct);

        if (page.TotalCount == 0)
            return "[Lịch] Chưa có lịch nào. Dùng schedule_task để tạo (cần người dùng phê duyệt).";

        var builder = new StringBuilder();
        builder.Append("[Lịch] ").Append(page.TotalCount).AppendLine(" lịch của người dùng (id — tên — chu kỳ — trạng thái):");
        foreach (var task in page.Items)
        {
            builder.Append("- ").Append(ShortId(task.Id)).Append(" — ").Append(task.Name)
                .Append(" — ").Append(DescribeCadence(task))
                .Append(" — ").Append(task.Enabled ? "đang bật" : "đã tắt");
            if (!string.IsNullOrEmpty(task.LastStatus))
                builder.Append(", lần cuối: ").Append(task.LastStatus);
            builder.AppendLine();
        }
        if (page.TotalCount > page.Items.Count)
            builder.Append("(còn ").Append(page.TotalCount - page.Items.Count).Append(" lịch nữa — xem đủ tại /schedules)");
        return builder.ToString().TrimEnd();
    }

    // ── cancel_schedule ─────────────────────────────────────────────────

    public async Task<string> CancelAsync(Guid tenantId, Guid userId, string input, CancellationToken ct)
    {
        var needle = input?.Trim() ?? string.Empty;
        if (needle.Length == 0)
            return "[Lịch] Cho tôi id (8 ký tự đầu) hoặc tên lịch cần tắt — xem bằng list_schedules.";

        // Chỉ lịch CỦA NGƯỜI DÙNG này; khớp id đầy đủ, tiền tố id, hoặc tên chứa chuỗi.
        var candidates = await _dbContext.ScheduledTasks
            .Where(task => task.TenantId == tenantId && task.UserId == userId)
            .Select(task => new { task.Id, task.Name, task.Enabled })
            .ToListAsync(ct);

        var matches = candidates.Where(task =>
                task.Id.ToString("N").StartsWith(needle.Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase)
                || task.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            return $"[Lịch] Không tìm thấy lịch nào khớp \"{needle}\". Dùng list_schedules để xem danh sách.";
        if (matches.Count > 1)
        {
            var options = string.Join("; ", matches.Take(5).Select(m => $"{ShortId(m.Id)} — {m.Name}"));
            return $"[Lịch] Có {matches.Count} lịch khớp \"{needle}\": {options}. Hãy chỉ định bằng id.";
        }

        var target = matches[0];
        if (!target.Enabled)
            return $"[Lịch] Lịch \"{target.Name}\" ({ShortId(target.Id)}) vốn đã tắt.";

        await _dbContext.ScheduledTasks
            .Where(task => task.Id == target.Id && task.TenantId == tenantId && task.UserId == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(task => task.Enabled, false), ct);

        _logger.LogInformation("📅 [ScheduleTool] Đã tắt lịch {TaskId} ({Name}) theo yêu cầu qua agent.", target.Id, target.Name);
        return $"[Lịch] Đã tắt lịch \"{target.Name}\" ({ShortId(target.Id)}). Bật lại được tại màn Task Scheduler (/schedules).";
    }

    // ── parsing / rendering ─────────────────────────────────────────────

    private sealed record CreateRequest(
        string Name, string Prompt, string Kind, string RunMode,
        int? IntervalMinutes, int? TimeOfDayMinutes, int? DayOfWeek, DateTime? RunAtUtc,
        Guid? CustomAgentId, string? DeliveryWebhookUrl);

    private static (CreateRequest? Request, string? Error) ParseCreateInput(string input)
    {
        var trimmed = input?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith('{'))
            return (null, "Payload phải là JSON: {\"name\",\"prompt\",\"kind\":\"interval|daily|weekly|once\",…}.");

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            var name = GetString(root, "name");
            var prompt = GetString(root, "prompt");
            if (string.IsNullOrWhiteSpace(name)) return (null, "Thiếu \"name\" (tên lịch).");
            if (string.IsNullOrWhiteSpace(prompt)) return (null, "Thiếu \"prompt\" (nội dung chạy mỗi lần).");

            var kind = (GetString(root, "kind") ?? "daily").Trim().ToLowerInvariant();
            var runMode = (GetString(root, "runMode") ?? "agent").Trim().ToLowerInvariant();

            int? interval = GetInt(root, "intervalMinutes");
            int? dayOfWeek = GetInt(root, "dayOfWeek");

            // "timeOfDayUtc": "HH:mm" — dạng agent viết tự nhiên nhất; đổi ra phút.
            int? timeOfDayMinutes = GetInt(root, "timeOfDayMinutes");
            var timeText = GetString(root, "timeOfDayUtc");
            if (timeOfDayMinutes is null && !string.IsNullOrWhiteSpace(timeText))
            {
                var parts = timeText.Split(':');
                if (parts.Length != 2
                    || !int.TryParse(parts[0], out var hh) || !int.TryParse(parts[1], out var mm)
                    || hh is < 0 or > 23 || mm is < 0 or > 59)
                {
                    return (null, $"\"timeOfDayUtc\" không hợp lệ ({timeText}) — định dạng HH:mm theo giờ UTC.");
                }
                timeOfDayMinutes = hh * 60 + mm;
            }

            DateTime? runAtUtc = null;
            var runAtText = GetString(root, "runAtUtc");
            if (!string.IsNullOrWhiteSpace(runAtText))
            {
                if (!DateTime.TryParse(runAtText, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var parsed))
                {
                    return (null, $"\"runAtUtc\" không hợp lệ ({runAtText}) — dùng ISO 8601, ví dụ 2026-09-15T01:00:00Z.");
                }
                runAtUtc = parsed;
            }

            Guid? customAgentId = null;
            var agentText = GetString(root, "customAgentId");
            if (!string.IsNullOrWhiteSpace(agentText))
            {
                if (!Guid.TryParse(agentText, out var parsedAgent))
                    return (null, "\"customAgentId\" phải là GUID của một agent trong Agent Studio.");
                customAgentId = parsedAgent;
            }

            var webhook = GetString(root, "deliveryWebhookUrl");

            return (new CreateRequest(
                name.Trim(), prompt.Trim(), kind, runMode,
                interval, timeOfDayMinutes, dayOfWeek, runAtUtc,
                customAgentId, string.IsNullOrWhiteSpace(webhook) ? null : webhook.Trim()), null);
        }
        catch (JsonException)
        {
            return (null, "JSON không hợp lệ. Ví dụ: {\"name\":\"Báo cáo sáng\",\"prompt\":\"…\",\"kind\":\"daily\",\"timeOfDayUtc\":\"01:00\"}.");
        }
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

    private static int? GetInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number ? prop.GetInt32() : null;

    private static string ShortId(Guid id) => id.ToString("N")[..8];

    private static string DescribeCadence(ScheduledTaskDto task) => task.ScheduleKind switch
    {
        ScheduleCalculator.IntervalKind => $"mỗi {task.IntervalMinutes} phút",
        ScheduleCalculator.DailyKind => $"hàng ngày {FormatTime(task.TimeOfDayMinutes)} UTC",
        ScheduleCalculator.WeeklyKind => $"hàng tuần thứ {(task.DayOfWeek is 0 ? "CN" : (task.DayOfWeek + 1)?.ToString())} {FormatTime(task.TimeOfDayMinutes)} UTC",
        ScheduleCalculator.OnceKind => $"một lần lúc {task.NextRunUtc:yyyy-MM-dd HH:mm} UTC",
        _ => task.ScheduleKind
    };

    private static string FormatTime(int? minutes)
    {
        var total = minutes ?? 0;
        return $"{total / 60:00}:{total % 60:00}";
    }
}
