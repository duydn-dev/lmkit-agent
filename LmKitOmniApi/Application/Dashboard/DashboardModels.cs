namespace LmKitOmniApi.Application.Dashboard;

/// <summary>
/// Cửa sổ thời gian của dashboard. Chỉ nhận 7/30/90 ngày — mọi giá trị khác (kể cả null,
/// số âm, hay số do client tự bịa) đều quy về 30. Chặn ở đây thay vì ở controller để cả
/// endpoint JSON lẫn CSV dùng chung một luật, không thể lệch nhau.
/// </summary>
public static class DashboardPeriod
{
    public const int DefaultDays = 30;

    public static readonly int[] AllowedDays = [7, 30, 90];

    /// <summary>Ngưỡng cảnh báo "sắp chạm hạn mức" (% đã dùng của hạn mức tháng).</summary>
    public const int OverThresholdPercent = 80;

    /// <summary>Số dòng tối đa cho các bảng xếp hạng (top user/đơn vị/hành động).</summary>
    public const int TopLimit = 8;

    /// <summary>Grant còn hạn trong bao nhiêu ngày thì được coi là "sắp hết hạn".</summary>
    public const int GrantExpiryHorizonDays = 30;

    public static int Normalize(int? days)
        => days is { } value && AllowedDays.Contains(value) ? value : DefaultDays;
}

/// <summary>Response đầy đủ của <c>GET /api/dashboard/stats</c>.</summary>
public sealed class DashboardStatsDto
{
    public int PeriodDays { get; set; }

    // Bốn con số cấp hệ thống, có với MỌI vai trò (Member cũng thấy quy mô hệ thống).
    public int TotalUsers { get; set; }
    public int TotalTenants { get; set; }
    public int TotalSessions { get; set; }
    public int TotalDocuments { get; set; }

    /// <summary>Khối biểu đồ — CHỈ Admin nhận được (Member luôn null), giống hệt cách
    /// web/API phía OllamaAgent phân quyền.</summary>
    public DashboardCockpitDto? Cockpit { get; set; }

    /// <summary>Usage của CHÍNH người đang đăng nhập — có với mọi vai trò.</summary>
    public DashboardSelfDto MyUsage { get; set; } = new();
}

public sealed class DashboardCockpitDto
{
    public int PeriodDays { get; set; }
    public DashboardTokensDto Tokens { get; set; } = new();
    public DashboardUsersDto Users { get; set; } = new();
    public DashboardSpendDto Spend { get; set; } = new();
    public DashboardQuotaDto Quota { get; set; } = new();
    public DashboardDocumentsDto Documents { get; set; } = new();
    public DashboardActivityDto Activity { get; set; } = new();
    public DashboardPerformanceDto Performance { get; set; } = new();
    public DashboardAlertsDto Alerts { get; set; } = new();
}

public sealed class DashboardTokensDto
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }

    /// <summary>Số lượt TRẢ LỜI (row assistant) trong kỳ — không đếm lượt hỏi để một hội
    /// thoại không bị tính hai lần chi phí.</summary>
    public int Messages { get; set; }

    /// <summary>
    /// Token ước lượng của các lượt AGENT-RUN trong kỳ, để RIÊNG khỏi lượt chat: một lần chạy
    /// agent là nhiều lượt suy luận liên tiếp nên tốn gấp nhiều lần một lượt chat, gộp chung sẽ
    /// giấu mất nguồn đốt token lớn nhất. Đây là số của cả lần chạy (cộng dồn mọi lần chạy lại
    /// sau phê duyệt), không phải của từng lượt.
    /// </summary>
    public int AgentRunPromptTokens { get; set; }

    /// <summary>Xem <see cref="AgentRunPromptTokens"/>.</summary>
    public int AgentRunCompletionTokens { get; set; }

    /// <summary>Số lần chạy THẬT SỰ gọi model trong kỳ (lần bị hàng đợi từ chối không tính).</summary>
    public int AgentRuns { get; set; }

    public List<DashboardDailyTokenDto> Daily { get; set; } = [];
    public List<DashboardModelUsageDto> ByModel { get; set; } = [];
}

public sealed class DashboardDailyTokenDto
{
    public string Date { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int Messages { get; set; }
}

public sealed class DashboardModelUsageDto
{
    public string ModelName { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int Messages { get; set; }
}

public sealed class DashboardUsersDto
{
    /// <summary>DAU/WAU/MAU LUÔN tính trên 1/7/30 ngày gần nhất, KHÔNG theo cửa sổ đang chọn
    /// — nhờ vậy ba số này so sánh được với nhau và với các dashboard quen thuộc.</summary>
    public int DailyActive { get; set; }
    public int WeeklyActive { get; set; }
    public int MonthlyActive { get; set; }

    public int TotalUsers { get; set; }
    public int NewUsers { get; set; }
    public int AdoptionPct { get; set; }
    public List<DashboardDailyCountDto> Daily { get; set; } = [];
    public List<DashboardTopUserDto> TopUsers { get; set; } = [];
}

public sealed class DashboardTopUserDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int Questions { get; set; }
    public int Answers { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
}

public sealed class DashboardSpendDto
{
    /// <summary>Tổng token của LƯỢT CHAT trong kỳ (không gồm agent-run — xem <see cref="TotalAgentRunTokens"/>).</summary>
    public int TotalTokens { get; set; }

    /// <summary>Tổng token của agent-run trong kỳ. Tách riêng, nhưng vẫn vào tỉ lệ tập trung chi tiêu.</summary>
    public int TotalAgentRunTokens { get; set; }

    /// <summary>Phần trăm token của 3 đơn vị đứng đầu trên tổng kỳ ("spend concentration").</summary>
    public int Top3SharePct { get; set; }

    public int TopTenantSharePct { get; set; }
    public string TopTenantName { get; set; } = string.Empty;
    public List<DashboardTenantUsageDto> ByTenant { get; set; } = [];
}

public sealed class DashboardTenantUsageDto
{
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int Messages { get; set; }

    /// <summary>Phần agent-run của đơn vị, tách khỏi lượt chat (xem DashboardTokensDto).</summary>
    public int AgentRunPromptTokens { get; set; }
    public int AgentRunCompletionTokens { get; set; }
    public int AgentRuns { get; set; }
}

public sealed class DashboardQuotaDto
{
    public int TenantsOnPlan { get; set; }
    public int TenantsOverThreshold { get; set; }
    public int TenantsOverLimit { get; set; }

    /// <summary>Đơn vị chưa gán gói nào — không có hạn mức để mà vượt.</summary>
    public int TenantsWithoutPlan { get; set; }

    public int TotalMonthlyLimit { get; set; }
    public int TotalMonthlyUsed { get; set; }
    public int TotalCreditBalance { get; set; }
    public List<DashboardTenantQuotaDto> ByTenant { get; set; } = [];
}

public sealed class DashboardTenantQuotaDto
{
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string? PlanName { get; set; }
    public int MonthlyLimit { get; set; }

    /// <summary>Token đã dùng của THÁNG DƯƠNG LỊCH hiện tại (UTC) — hạn mức gói reset theo
    /// tháng nên con số này không phụ thuộc cửa sổ 7/30/90 đang chọn.</summary>
    public int UsedTokens { get; set; }

    public int UtilizationPct { get; set; }
    public int CreditBalance { get; set; }
    public DateTime? RenewalAtUtc { get; set; }

    /// <summary>Gói không giới hạn (MonthlyTokenLimit &lt;= 0) — không tính vào % hạn mức.</summary>
    public bool IsUnlimited { get; set; }
}

public sealed class DashboardDocumentsDto
{
    public int Total { get; set; }
    public int Indexed { get; set; }
    public int Pending { get; set; }
    public int Failed { get; set; }
    public int TotalChunks { get; set; }
    public List<DashboardCountDto> ByStatus { get; set; } = [];

    /// <summary>Nhóm theo nguồn tài liệu (upload mặc định / OCR / knowledge base...).</summary>
    public List<DashboardCountDto> BySource { get; set; } = [];
}

public sealed class DashboardCountDto
{
    public string Key { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class DashboardActivityDto
{
    public int Total { get; set; }
    public List<DashboardDailyCountDto> Daily { get; set; } = [];
    public List<DashboardCountDto> TopActions { get; set; } = [];
}

public sealed class DashboardDailyCountDto
{
    public string Date { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class DashboardPerformanceDto
{
    /// <summary>Số mẫu có ghi độ trễ. 0 = chưa có lượt nào được đo (mọi số khác vô nghĩa).</summary>
    public int Samples { get; set; }
    public int AvgLatencyMs { get; set; }
    public int P95LatencyMs { get; set; }

    /// <summary>
    /// Độ trễ của các lần chạy agent trong kỳ, đo riêng vì một lần chạy gồm nhiều lượt suy luận
    /// cộng lại — trộn với lượt chat sẽ ra một con số không mô tả cái gì cả.
    /// </summary>
    public int AgentRunSamples { get; set; }
    public int AgentRunAvgLatencyMs { get; set; }
    public int AgentRunP95LatencyMs { get; set; }
}

public sealed class DashboardAlertsDto
{
    public int NearLimitTenants { get; set; }
    public int OverLimitTenants { get; set; }
    public int ExpiringGrantCount { get; set; }
    public List<DashboardExpiringGrantDto> ExpiringGrants { get; set; } = [];
}

public sealed class DashboardExpiringGrantDto
{
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public int RemainingTokens { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public int DaysLeft { get; set; }
}

public sealed class DashboardSelfDto
{
    public int Questions { get; set; }
    public int Answers { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int Sessions { get; set; }
    public List<DashboardDailyCountDto> Daily { get; set; } = [];
}
