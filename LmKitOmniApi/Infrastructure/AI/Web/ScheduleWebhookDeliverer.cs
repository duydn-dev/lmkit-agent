using System.Text;
using System.Text.Json;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.Web;

/// <summary>
/// Cấu hình kênh giao kết quả lịch qua webhook, bound từ "ScheduleWebhooks".
/// TẮT mặc định — đây là egress do NGƯỜI DÙNG khai URL, cùng posture với
/// WebRead/ApiTool/DatabaseAgent: vận hành phải chủ động bật.
/// </summary>
public sealed class ScheduleWebhookOptions
{
    public const string SectionName = "ScheduleWebhooks";

    /// <summary>Master switch. False (mặc định) = không lưu được URL webhook và không gửi.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Allowlist host tùy chọn (trống = mọi host CÔNG KHAI; SSRF vẫn chặn nội bộ).
    /// Khớp đúng host hoặc subdomain của một mục.
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>Ngân sách một lần gửi, giây.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Trần ký tự của trường result trong payload.</summary>
    public int MaxResultChars { get; set; } = 32_768;
}

/// <summary>
/// Gửi kết quả một lần chạy lịch THÀNH CÔNG tới webhook của lịch (POST JSON
/// {taskId, taskName, runMode, status, completedAtUtc, result}). An toàn egress
/// cùng khuôn call_api: pre-flight <see cref="ToolSandboxService.ValidateUrlAsync"/>
/// (URL + DNS), HttpClient riêng với SsrfSafeConnect re-vet lúc connect và không
/// auto-redirect, allowlist host tùy chọn, result cắt trần. Trả về null khi gửi
/// thành công, ngược lại là MÔ TẢ LỖI ngắn (tiếng Việt) để worker nối vào
/// notification — giao webhook thất bại không bao giờ làm hỏng kết quả run.
/// </summary>
public sealed class ScheduleWebhookDeliverer
{
    public const string HttpClientName = "ScheduleWebhook";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ToolSandboxService _sandbox;
    private readonly ScheduleWebhookOptions _options;
    private readonly ILogger<ScheduleWebhookDeliverer> _logger;

    public ScheduleWebhookDeliverer(
        IHttpClientFactory httpClientFactory,
        ToolSandboxService sandbox,
        IOptions<ScheduleWebhookOptions> options,
        ILogger<ScheduleWebhookDeliverer> logger)
    {
        _httpClientFactory = httpClientFactory;
        _sandbox = sandbox;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => _options.Enabled;

    /// <summary>Kiểm tra allowlist host — dùng chung bởi save-validation và lúc gửi.</summary>
    public static string? ValidateAllowedHost(string url, ScheduleWebhookOptions options)
    {
        if (options.AllowedHosts.Count == 0) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "URL webhook không hợp lệ.";

        foreach (var allowed in options.AllowedHosts)
        {
            var entry = allowed?.Trim().TrimStart('.');
            if (string.IsNullOrEmpty(entry)) continue;
            if (uri.Host.Equals(entry, StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("." + entry, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        return $"Host \"{uri.Host}\" không nằm trong allowlist ScheduleWebhooks:AllowedHosts.";
    }

    /// <summary>
    /// Gửi payload; null = thành công, chuỗi = lý do thất bại (đã an toàn để hiển thị).
    /// URL được RE-VALIDATE mỗi lần gửi (không tin giá trị đã lưu): feature có thể
    /// vừa bị tắt, allowlist có thể vừa đổi, DNS có thể đã trỏ về nội bộ.
    /// </summary>
    public async Task<string?> DeliverAsync(
        Guid taskId, string taskName, string runMode, string result, DateTime completedAtUtc,
        string webhookUrl, CancellationToken ct)
    {
        if (!_options.Enabled)
            return "kênh webhook đang tắt (ScheduleWebhooks:Enabled=false)";

        var hostError = ValidateAllowedHost(webhookUrl, _options);
        if (hostError is not null) return hostError;

        var validation = await _sandbox.ValidateUrlAsync(webhookUrl, ct);
        if (!validation.IsAllowed) return validation.DenialReason ?? "URL webhook bị chặn";

        var payload = JsonSerializer.Serialize(new
        {
            taskId,
            taskName,
            runMode,
            status = "Succeeded",
            completedAtUtc,
            result = result.Length <= _options.MaxResultChars ? result : result[.._options.MaxResultChars]
        });

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 60)));

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(webhookUrl, content, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "📮 [ScheduleWebhook] Task {TaskId}: webhook trả về {Status}.", taskId, (int)response.StatusCode);
                return $"webhook trả về HTTP {(int)response.StatusCode}";
            }

            _logger.LogInformation("📮 [ScheduleWebhook] Task {TaskId}: đã giao kết quả.", taskId);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return $"hết thời gian chờ ({_options.TimeoutSeconds}s)";
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "📮 [ScheduleWebhook] Task {TaskId}: gửi thất bại.", taskId);
            return "không kết nối được tới webhook";
        }
    }
}
