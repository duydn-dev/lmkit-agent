using LmKitOmniApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Quotas;

/// <summary>
/// Nguồn DUY NHẤT của câu hỏi "đơn vị này đã dùng bao nhiêu token trong tháng dương lịch hiện
/// tại". Cả dashboard vận hành (khối hạn mức) lẫn worker cảnh báo chủ động đều hỏi ở đây, nên
/// con số hiển thị trên màn hình và con số dùng để bắn thông báo không thể lệch nhau.
///
/// Gồm HAI nguồn chi phí, và phải gồm cả hai:
/// <list type="bullet">
///   <item>lượt chat — row assistant của <c>ChatMessage</c>;</item>
///   <item>lần chạy agent — <c>AgentRun</c>, đã cộng dồn token của mọi lần chạy lại sau phê duyệt.</item>
/// </list>
/// Một lần chạy agent là nhiều lượt suy luận liên tiếp nên tốn gấp nhiều lần một lượt chat. Chỉ
/// đếm chat thì một đơn vị đốt hạn mức qua agent sẽ hiển thị "còn thoải mái" cho tới khi vượt
/// hạn mức thật — tức là cảnh báo 80% không bao giờ bắn đúng lúc.
///
/// Hạn mức gói reset theo tháng dương lịch (UTC) chứ KHÔNG theo cửa sổ 7/30/90 của dashboard:
/// đổi bộ lọc thời gian không được làm % hạn mức nhảy múa dù hạn mức không hề thay đổi.
/// </summary>
public static class TenantQuotaUsage
{
    /// <summary>Mốc 00:00 ngày 1 của tháng chứa <paramref name="now"/>, theo UTC.</summary>
    public static DateTime MonthStartUtc(DateTime now)
        => new(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Token đã dùng từ <paramref name="monthStartUtc"/> trở đi, theo từng đơn vị. Đơn vị chưa
    /// tốn token nào KHÔNG có mặt trong dictionary (người gọi tự coi như 0).
    /// </summary>
    public static async Task<Dictionary<Guid, int>> GetMonthlyUsedByTenantAsync(
        HermesDbContext dbContext,
        DateTime monthStartUtc,
        CancellationToken ct)
    {
        var chatUsage = await dbContext.ChatMessages.AsNoTracking()
            .Where(m => m.Role == "assistant" && m.CreatedAt >= monthStartUtc)
            .GroupBy(m => m.ChatSession!.TenantId)
            .Select(g => new { TenantId = g.Key, Used = g.Sum(x => x.PromptTokens + x.CompletionTokens) })
            .ToListAsync(ct);

        // Một lần chạy được xếp vào tháng theo CreatedAtUtc (lúc BẮT ĐẦU), nên lần chạy kéo dài
        // qua nửa đêm vẫn chỉ thuộc đúng một tháng chứ không bị chẻ đôi.
        var agentUsage = await dbContext.AgentRuns.AsNoTracking()
            .Where(r => r.CreatedAtUtc >= monthStartUtc)
            .GroupBy(r => r.TenantId)
            .Select(g => new { TenantId = g.Key, Used = g.Sum(x => x.PromptTokens + x.CompletionTokens) })
            .ToListAsync(ct);

        var used = new Dictionary<Guid, int>();
        foreach (var row in chatUsage) used[row.TenantId] = row.Used;
        foreach (var row in agentUsage)
        {
            used[row.TenantId] = used.TryGetValue(row.TenantId, out var existing)
                ? existing + row.Used
                : row.Used;
        }

        return used;
    }
}
