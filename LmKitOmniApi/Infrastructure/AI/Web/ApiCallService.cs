using System.Text;
using System.Text.Json;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.Web;

/// <summary>
/// Configuration for the generic REST tool (<c>call_api</c> / <c>call_api_write</c>).
/// Bound from the "ApiTool" section. DISABLED BY DEFAULT — calling arbitrary APIs is
/// outbound egress, same posture as WebRead/DatabaseAgent: an operator must opt in.
/// </summary>
public sealed class ApiCallOptions
{
    public const string SectionName = "ApiTool";

    /// <summary>Master switch. False (default) = the call_api tools are never offered.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Optional host allowlist. Empty = any PUBLIC host (SSRF checks still apply).
    /// Non-empty = the request host must equal an entry or be a subdomain of one
    /// (entry "api.example.com" matches itself and "v2.api.example.com").
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>Cap on the response text returned to the agent (it lands in model context).</summary>
    public int MaxResponseChars { get; set; } = 8_000;

    /// <summary>Cap on the outgoing request body, in characters.</summary>
    public int MaxRequestBodyChars { get; set; } = 32_768;

    /// <summary>Wall-clock budget for one call, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Generic REST caller behind the agent tools <c>call_api</c> (GET/HEAD — read-only)
/// and <c>call_api_write</c> (POST/PUT/PATCH/DELETE — approval-required qua HITL).
///
/// An ninh nhiều lớp, cùng khuôn với fetch_web/MCP:
/// <list type="bullet">
///   <item>Feature flag tắt mặc định + allowlist host tùy chọn (ApiTool section).</item>
///   <item>Pre-flight <see cref="ToolSandboxService.ValidateUrlAsync"/> (chặn IP nội
///   bộ/loopback/metadata cả ở tầng DNS).</item>
///   <item>HttpClient riêng với <c>SsrfSafeConnect</c> ConnectCallback — re-vet địa chỉ
///   NGAY LÚC connect, đóng cửa sổ DNS-rebinding; không auto-redirect (3xx được báo
///   lại chứ không bao giờ tự đi theo).</item>
///   <item>Header theo allowlist ngắn; body/response đều bị cắt trần.</item>
///   <item>Phương thức ghi tách thành tool riêng nằm trong ApprovalRequiredTools —
///   không bao giờ chạy khi chưa có người phê duyệt.</item>
/// </list>
/// </summary>
public sealed class ApiCallService
{
    public const string HttpClientName = "ApiTool";

    /// <summary>Header cho phép agent đặt (so khớp không phân biệt hoa thường).</summary>
    private static readonly HashSet<string> AllowedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Content-Type", "Authorization", "X-Api-Key", "X-Request-Id"
    };

    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD" };
    private static readonly HashSet<string> WriteMethods = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    private const int MaxHeaders = 8;
    private const int MaxHeaderValueLength = 512;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ToolSandboxService _sandbox;
    private readonly ApiCallOptions _options;
    private readonly ILogger<ApiCallService> _logger;

    public ApiCallService(
        IHttpClientFactory httpClientFactory,
        ToolSandboxService sandbox,
        IOptions<ApiCallOptions> options,
        ILogger<ApiCallService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _sandbox = sandbox;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => _options.Enabled;

    /// <summary>
    /// Payload từ agent: hoặc một URL trần (→ GET), hoặc JSON
    /// <c>{"method","url","headers":{...},"body":"..."}</c>. <paramref name="allowWrite"/>
    /// phản ánh tool đã gọi: <c>call_api</c> chỉ nhận GET/HEAD, <c>call_api_write</c>
    /// chỉ nhận POST/PUT/PATCH/DELETE (đã qua phê duyệt HITL trước khi tới đây).
    /// Mọi lỗi trả về chuỗi "[API] …" cho agent thay vì ném exception.
    /// </summary>
    public async Task<string> ExecuteAsync(string input, bool allowWrite, CancellationToken ct)
    {
        if (!IsEnabled)
            return "[API] Tool gọi API chưa được bật. Quản trị viên cần bật mục cấu hình ApiTool.";

        var (request, parseError) = ParseRequest(input);
        if (parseError is not null) return $"[API] {parseError}";

        var method = request!.Method.ToUpperInvariant();
        if (allowWrite)
        {
            if (!WriteMethods.Contains(method))
                return "[API] call_api_write chỉ dành cho POST/PUT/PATCH/DELETE. Với GET/HEAD hãy dùng call_api.";
        }
        else if (!SafeMethods.Contains(method))
        {
            return "[API] call_api chỉ cho phép GET/HEAD (chỉ đọc). Yêu cầu ghi phải dùng call_api_write và sẽ cần người dùng phê duyệt.";
        }

        var hostError = ValidateAllowedHost(request.Url);
        if (hostError is not null) return $"[API] {hostError}";

        // Pre-flight SSRF (URL + mọi địa chỉ DNS); socket ConnectCallback sẽ re-vet lần
        // nữa lúc connect nên DNS-rebinding giữa hai thời điểm cũng không thoát.
        var validation = await _sandbox.ValidateUrlAsync(request.Url, ct);
        if (!validation.IsAllowed) return $"[API] {validation.DenialReason}";

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 120)));

            using var httpRequest = BuildHttpRequest(request, method);
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            return await FormatResponseAsync(response, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return $"[API] Hết thời gian chờ ({_options.TimeoutSeconds}s) khi gọi {request.Url}.";
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "🌐 [ApiTool] Request failed for {Url}", request.Url);
            return $"[API] Gọi thất bại: {Truncate(ex.Message, 300)}";
        }
    }

    // ── parsing ─────────────────────────────────────────────────────────

    private sealed record ParsedRequest(string Method, string Url, Dictionary<string, string> Headers, string? Body);

    private (ParsedRequest? Request, string? Error) ParseRequest(string input)
    {
        var trimmed = input?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return (null, "Thiếu nội dung. Truyền URL, hoặc JSON {\"method\",\"url\",\"headers\",\"body\"}.");

        if (!trimmed.StartsWith('{'))
            return (new ParsedRequest("GET", trimmed, [], null), null);

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            var url = root.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) return (null, "JSON thiếu trường \"url\".");

            var method = root.TryGetProperty("method", out var methodProp)
                ? methodProp.GetString()?.Trim() ?? "GET"
                : "GET";
            if (!SafeMethods.Contains(method) && !WriteMethods.Contains(method))
                return (null, $"Phương thức \"{method}\" không được hỗ trợ (GET/HEAD/POST/PUT/PATCH/DELETE).");

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("headers", out var headersProp) && headersProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var header in headersProp.EnumerateObject())
                {
                    if (headers.Count >= MaxHeaders)
                        return (null, $"Tối đa {MaxHeaders} header.");
                    if (!AllowedRequestHeaders.Contains(header.Name))
                        return (null, $"Header \"{header.Name}\" không nằm trong danh sách cho phép ({string.Join(", ", AllowedRequestHeaders)}).");
                    var value = header.Value.GetString() ?? string.Empty;
                    if (value.Length > MaxHeaderValueLength)
                        return (null, $"Giá trị header \"{header.Name}\" vượt {MaxHeaderValueLength} ký tự.");
                    headers[header.Name] = value;
                }
            }

            string? body = null;
            if (root.TryGetProperty("body", out var bodyProp) && bodyProp.ValueKind != JsonValueKind.Null)
            {
                // Chuỗi giữ nguyên; object/array serialize lại thành JSON.
                body = bodyProp.ValueKind == JsonValueKind.String ? bodyProp.GetString() : bodyProp.GetRawText();
                if (body is not null && body.Length > _options.MaxRequestBodyChars)
                    return (null, $"Body vượt giới hạn {_options.MaxRequestBodyChars} ký tự.");
            }

            return (new ParsedRequest(method, url.Trim(), headers, body), null);
        }
        catch (JsonException)
        {
            return (null, "JSON không hợp lệ. Định dạng: {\"method\":\"GET\",\"url\":\"https://…\",\"headers\":{…},\"body\":…}.");
        }
    }

    private string? ValidateAllowedHost(string url)
    {
        if (_options.AllowedHosts.Count == 0) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "URL không hợp lệ.";

        var host = uri.Host;
        foreach (var allowed in _options.AllowedHosts)
        {
            var entry = allowed?.Trim().TrimStart('.');
            if (string.IsNullOrEmpty(entry)) continue;
            if (host.Equals(entry, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + entry, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        return $"Host \"{host}\" không nằm trong allowlist ApiTool:AllowedHosts.";
    }

    private HttpRequestMessage BuildHttpRequest(ParsedRequest request, string method)
    {
        var httpRequest = new HttpRequestMessage(new HttpMethod(method), request.Url);

        string? contentType = null;
        foreach (var (name, value) in request.Headers)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = value;
                continue; // content header — đặt lên StringContent bên dưới
            }
            httpRequest.Headers.TryAddWithoutValidation(name, value);
        }

        if (request.Body is not null && WriteMethods.Contains(method))
        {
            httpRequest.Content = new StringContent(
                request.Body, Encoding.UTF8, contentType ?? "application/json");
        }

        return httpRequest;
    }

    private async Task<string> FormatResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var builder = new StringBuilder();
        builder.Append("HTTP ").Append(status).Append(' ').AppendLine(response.ReasonPhrase);

        var contentType = response.Content.Headers.ContentType?.ToString();
        if (!string.IsNullOrEmpty(contentType))
            builder.Append("Content-Type: ").AppendLine(contentType);

        // Không bao giờ tự đi theo redirect (client tắt AllowAutoRedirect): báo đích
        // để agent quyết định gọi tiếp — URL mới sẽ lại qua đủ các lớp kiểm tra.
        if (status is >= 300 and < 400 && response.Headers.Location is { } location)
            builder.Append("Location: ").AppendLine(location.ToString());

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!string.IsNullOrWhiteSpace(body))
        {
            builder.AppendLine();
            builder.Append(Truncate(body.Trim(), Math.Max(1, _options.MaxResponseChars)));
            if (body.Length > _options.MaxResponseChars)
                builder.Append("\n… (đã cắt bớt ").Append(body.Length - _options.MaxResponseChars).Append(" ký tự)");
        }

        return builder.ToString();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
