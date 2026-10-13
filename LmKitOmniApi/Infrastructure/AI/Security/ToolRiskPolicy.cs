using System.Collections.Concurrent;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.ToolPolicy;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Infrastructure.AI.Security;

// Chính sách rủi ro tool theo đơn vị: đọc tenant_tool_policies, áp lên mức mặc định của hệ thống.

/// <summary>
/// Đọc chính sách tool theo đơn vị từ <c>tenant_tool_policies</c> và áp lên mức mặc định.
///
/// <para>
/// <b>Vì sao có cache.</b> Cửa kiểm tra này chạy cho MỖI lần gọi tool của agent — nhiều lần trong
/// một lượt chat, nhiều lượt mỗi phút. Một truy vấn DB cho mỗi tool call là chi phí không cần thiết
/// cho dữ liệu gần như không bao giờ đổi, nên cấu hình của một đơn vị được giữ tối đa
/// <see cref="CacheTtl"/> và bị xoá ngay khi chính replica này ghi cấu hình.
/// </para>
///
/// <para>
/// <b>Đánh đổi được nói rõ:</b> một replica khác sẽ áp cấu hình mới sau tối đa một TTL (30 giây).
/// Chiều trễ này là chiều an toàn: cấu hình chỉ được phép SIẾT chặt hơn mức mặc định, nên trong
/// khoảng trễ đó hệ thống chạy ở mức mặc định chứ không chạy ở mức lỏng hơn.
/// </para>
///
/// <para>
/// <b>Singleton.</b> Lớp này giữ cache nên được đăng ký singleton; nó tự mở scope để lấy
/// <see cref="HermesDbContext"/> vì DbContext là scoped. <see cref="IToolPermissionService"/> cũng
/// là singleton (giữ bộ đếm tần suất), nên nếu lớp này scoped thì không tiêm được vào đó.
/// </para>
/// </summary>
public sealed class ToolRiskPolicy : IToolRiskPolicy
{
    /// <summary>Thời gian sống của cấu hình đã cache cho một đơn vị.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private sealed record CacheEntry(DateTime ExpiresAtUtc, Dictionary<string, TenantToolPolicy> Overrides);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ToolRiskPolicy> _logger;
    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();

    public ToolRiskPolicy(IServiceScopeFactory scopeFactory, ILogger<ToolRiskPolicy> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<ToolRiskDecision> ResolveAsync(
        Guid tenantId, string toolName, CancellationToken ct = default)
    {
        var baseline = ToolRiskDecision.Default(toolName);
        var overrides = await LoadOverridesAsync(tenantId, ct);
        if (overrides.Count == 0) return baseline;

        return overrides.TryGetValue(toolName, out var policy)
            ? ToolRiskDecision.Apply(baseline, policy.Enforcement, policy.Reason)
            : baseline;
    }

    public async Task<IReadOnlyDictionary<string, TenantToolPolicy>> ListOverridesAsync(
        Guid tenantId, CancellationToken ct = default)
        => await LoadOverridesAsync(tenantId, ct);

    public void Invalidate(Guid tenantId) => _cache.TryRemove(tenantId, out _);

    private async Task<Dictionary<string, TenantToolPolicy>> LoadOverridesAsync(Guid tenantId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty) return [];

        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(tenantId, out var cached) && cached.ExpiresAtUtc > now)
        {
            return cached.Overrides;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();

            var rows = await db.Set<TenantToolPolicy>()
                .AsNoTracking()
                .Where(p => p.TenantId == tenantId)
                .ToListAsync(ct);

            var map = rows.ToDictionary(p => p.ToolName, p => p, StringComparer.OrdinalIgnoreCase);
            _cache[tenantId] = new CacheEntry(now.Add(CacheTtl), map);
            return map;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Không đọc được cấu hình → chạy ở mức MẶC ĐỊNH (chặt nhất theo phân loại nền). Không
            // cache kết quả lỗi để lần gọi sau thử lại ngay.
            _logger.LogError(ex,
                "Không đọc được chính sách tool của đơn vị {TenantId}; áp mức mặc định.", tenantId);
            return [];
        }
    }
}
