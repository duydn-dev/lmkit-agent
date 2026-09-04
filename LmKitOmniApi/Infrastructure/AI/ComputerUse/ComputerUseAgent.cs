using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LmKitOmniApi.Infrastructure.AI.Observability;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.ComputerUse;

/// <summary>
/// The computer-use loop. Each step: observe → ask the model for ONE action → parse it →
/// refuse credential/CAPTCHA actions (hand off) → enforce the navigation allowlist →
/// gate side-effecting actions on human approval → execute → observe again. The loop
/// stops on <c>done</c>/<c>ask</c>, on a refused/unapproved action, at the step cap, or
/// at the per-session wall-clock cap.
///
/// Every collaborator is a seam (<see cref="IComputerUseExecutor"/>,
/// <see cref="IComputerUseModel"/>, <see cref="IComputerUseApprovalGate"/>) so the whole
/// loop is unit-testable with a fake browser + scripted model + scripted approver, with
/// NO container and NO model load.
/// </summary>
public sealed class ComputerUseAgent : IComputerUseAgent
{
    /// <summary>
    /// The system prompt: the action schema plus the NON-NEGOTIABLE refusal rules. The
    /// <see cref="ComputerUseSafetyGuard"/> enforces the same rules even if the model
    /// ignores them.
    /// </summary>
    public const string SystemPrompt =
        "You are a careful web automation agent. Each turn you receive the current page (url, " +
        "title, a numbered list of interactive elements) and a screenshot, and you choose EXACTLY " +
        "ONE next action, returned as a single JSON object and nothing else.\n" +
        "Actions:\n" +
        "  {\"action\":\"navigate\",\"url\":\"https://…\"}\n" +
        "  {\"action\":\"click\",\"ref\":<n>}            // prefer ref over x/y\n" +
        "  {\"action\":\"type\",\"ref\":<n>,\"text\":\"…\"}\n" +
        "  {\"action\":\"key\",\"keys\":\"Enter\"}\n" +
        "  {\"action\":\"scroll\",\"direction\":\"down\",\"amount\":3}\n" +
        "  {\"action\":\"wait\",\"ms\":500}\n" +
        "  {\"action\":\"screenshot\"}                    // re-observe without acting\n" +
        "  {\"action\":\"done\",\"summary\":\"…\"}          // task finished\n" +
        "  {\"action\":\"ask\",\"question\":\"…\"}          // hand back to the human\n" +
        "HARD RULES — you MUST obey:\n" +
        "  • NEVER type passwords, credentials, payment/card details, OTP/2FA codes, or any secret. " +
        "If a step needs one, use \"ask\" so the human can do it.\n" +
        "  • NEVER attempt to solve or bypass a CAPTCHA or bot-detection challenge. Use \"ask\".\n" +
        "  • NEVER try to create accounts, log in on the user's behalf, or accept legal/consent terms.\n" +
        "  • Prefer element refs over coordinates. When the task is complete, return \"done\".";

    private readonly IComputerUseExecutor _executor;
    private readonly IComputerUseModel _model;
    private readonly IComputerUseApprovalGate _approvalGate;
    private readonly ComputerUseOptions _options;
    private readonly UserResourceAccessService _resources;
    private readonly AgentToolAuditService? _audit;
    private readonly ILogger<ComputerUseAgent> _logger;

    private const int MaxHistoryLines = 10;
    private const string AuditToolName = "COMPUTER_USE";

    public ComputerUseAgent(
        IComputerUseExecutor executor,
        IComputerUseModel model,
        IComputerUseApprovalGate approvalGate,
        IOptions<ComputerUseOptions> options,
        UserResourceAccessService resources,
        ILogger<ComputerUseAgent> logger,
        AgentToolAuditService? audit = null)
    {
        _executor = executor;
        _model = model;
        _approvalGate = approvalGate;
        _options = options.Value;
        _resources = resources;
        _logger = logger;
        _audit = audit;
    }

    /// <inheritdoc />
    public bool IsEnabled => _executor.IsEnabled;

    /// <inheritdoc />
    public async IAsyncEnumerable<string> RunAsync(
        ComputerUseRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // First event: the session id so the client can correlate + resolve approvals.
        yield return $"[COMPUTER_USE:{request.SessionId}]";

        if (!IsEnabled)
        {
            yield return "[THINKING]: ⚠️ Công cụ điều khiển trình duyệt chưa được bật.\\n";
            yield break;
        }

        // Per-session wall-clock cap: a linked source that also fires on the configured budget.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sessionCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.SessionWallClockSeconds)));
        var sct = sessionCts.Token;

        var sessionDir = Path.Combine(Path.GetTempPath(), "lmkit-computeruse", request.SessionId.ToString("N"));
        var history = new List<string>();
        var ordinal = 0;
        ComputerUseObservation observation = new();

        try
        {
            Directory.CreateDirectory(sessionDir);

            // ── Initial navigation to the user-supplied start URL (user's own input:
            //    allowlist + SSRF gated by the executor, but not approval-gated) ──
            if (!string.IsNullOrWhiteSpace(request.StartUrl))
            {
                yield return $"[THINKING]: 🌐 Mở trang bắt đầu: {request.StartUrl}\\n";
                var navAction = new ComputerUseAction { Type = ComputerUseActionType.Navigate, Url = request.StartUrl };
                var (startObs, cancelled) = await TryStepAsync(navAction, request, sessionDir, sct);
                if (cancelled) yield break;
                observation = startObs!;
                ordinal++;
                await AuditAsync(request, navAction, observation.IsError ? "failed" : "succeeded");
                history.Add(Summarize(navAction, observation));
                foreach (var marker in RenderStep(ordinal, navAction, observation, request)) yield return marker;
                if (observation.IsError)
                {
                    yield return $"[THINKING]: ⚠️ Không mở được trang bắt đầu: {observation.Error}\\n";
                    yield break; // cannot proceed without a page
                }
            }

            // ── Main perception→action loop ──
            for (var i = 0; i < _options.MaxSteps; i++)
            {
                if (sct.IsCancellationRequested)
                {
                    yield return "[THINKING]: ⏹️ Phiên vượt quá thời gian cho phép — dừng lại.\\n";
                    yield break;
                }

                var screenshotPath = ResolveScreenshotPath(observation.ScreenshotFileId, request);
                var prompt = new ComputerUsePrompt(request.TaskGoal, SystemPrompt, observation, history, screenshotPath);

                var (raw, decided) = await TryDecideAsync(prompt, sct);
                if (!decided)
                {
                    yield return "[THINKING]: ⚠️ Không lấy được hành động từ mô hình — dừng lại.\\n";
                    yield break;
                }

                if (!ComputerUseActionParser.TryParse(raw, out var action, out var parseError) || action is null)
                {
                    yield return $"[THINKING]: ⚠️ Không phân tích được hành động ({parseError}) — thử lại.\\n";
                    history.Add($"invalid action rejected: {parseError}");
                    TrimHistory(history);
                    continue; // a malformed action still consumes a step, so the loop can't spin forever
                }

                // ── HARD refusal: credentials / CAPTCHA → hand off, never execute, never gate ──
                var refusal = ComputerUseSafetyGuard.RequiresHumanHandoff(action, observation);
                if (refusal is not null)
                {
                    _logger.LogWarning("🛑 [ComputerUse] Từ chối hành động nhạy cảm: {Reason}", refusal);
                    await AuditAsync(request, action, "refused_handoff");
                    yield return $"[THINKING]: 🛑 {refusal}\\n";
                    yield return "Tôi không thể tự thực hiện bước này (nhập thông tin đăng nhập/thanh toán hoặc giải CAPTCHA). "
                                 + "Vui lòng tự thao tác bước đó rồi yêu cầu tôi tiếp tục.";
                    yield break;
                }

                // ── Terminal actions ──
                if (action.Type == ComputerUseActionType.Done)
                {
                    await AuditAsync(request, action, "done");
                    yield return "[THINKING]: ✅ Đã hoàn tất nhiệm vụ.\\n";
                    if (!string.IsNullOrWhiteSpace(action.Summary)) yield return action.Summary!;
                    yield break;
                }
                if (action.Type == ComputerUseActionType.Ask)
                {
                    await AuditAsync(request, action, "ask");
                    yield return "[THINKING]: ❓ Cần con người hỗ trợ.\\n";
                    if (!string.IsNullOrWhiteSpace(action.Question)) yield return action.Question!;
                    yield break;
                }

                // ── Navigation allowlist (defense-in-depth; the executor re-checks) ──
                if (action.Type == ComputerUseActionType.Navigate && !IsNavigationAllowed(action.Url))
                {
                    _logger.LogWarning("🔒 [ComputerUse] Điều hướng bị chặn (không nằm trong allowlist): {Url}", action.Url);
                    await AuditAsync(request, action, "navigation_denied");
                    yield return $"[THINKING]: 🔒 Điều hướng tới '{action.Url}' không được phép (ngoài danh sách cho phép).\\n";
                    history.Add($"navigation to {action.Url} refused (not allowlisted)");
                    TrimHistory(history);
                    continue;
                }

                // ── Approval gate for side-effecting actions ──
                if (action.IsSideEffecting && _options.RequireApprovalPerAction)
                {
                    var approvalId = Guid.NewGuid();
                    yield return $"[HITL_APPROVAL_REQUIRED:{approvalId}]";
                    yield return $"[THINKING]: ⏳ Chờ phê duyệt: {action.Describe()}\\n";

                    var request2 = new ComputerUseApprovalRequest(
                        approvalId, request.TenantId, request.UserId, request.SessionId,
                        action.Describe(), BuildApprovalDetails(action, observation));
                    var (approved, approvalCancelled) = await TryApproveAsync(request2, sct);
                    if (approvalCancelled) yield break;
                    if (!approved)
                    {
                        await AuditAsync(request, action, "not_approved");
                        yield return "[THINKING]: 🚫 Hành động không được phê duyệt — dừng lại.\\n";
                        yield break;
                    }
                }

                // ── Execute ──
                var (stepObs, stepCancelled) = await TryStepAsync(action, request, sessionDir, sct);
                if (stepCancelled) yield break;
                observation = stepObs!;
                ordinal++;
                await AuditAsync(request, action, observation.IsError ? "failed" : "succeeded");
                history.Add(Summarize(action, observation));
                TrimHistory(history);
                foreach (var marker in RenderStep(ordinal, action, observation, request)) yield return marker;
                if (observation.IsError)
                    yield return $"[THINKING]: ⚠️ {observation.Error}\\n";
            }

            yield return $"[THINKING]: ⏹️ Đã đạt giới hạn {_options.MaxSteps} bước — dừng lại.\\n";
        }
        finally
        {
            TryDeleteDirectory(sessionDir);
        }
    }

    // ── Exception-safe collaborator calls (an iterator cannot yield inside try/catch) ──

    private async Task<(string? Raw, bool Decided)> TryDecideAsync(ComputerUsePrompt prompt, CancellationToken ct)
    {
        try { return (await _model.DecideNextActionAsync(prompt, ct), true); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (null, false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "❌ [ComputerUse] Lỗi khi lấy hành động từ mô hình.");
            return (null, false);
        }
    }

    private async Task<(ComputerUseObservation? Observation, bool Cancelled)> TryStepAsync(
        ComputerUseAction action, ComputerUseRequest request, string sessionDir, CancellationToken ct)
    {
        try
        {
            var obs = await _executor.StepAsync(action, request.TenantId, request.UserId, sessionDir, ct);
            return (obs, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "❌ [ComputerUse] Lỗi khi thực thi bước.");
            return (ComputerUseObservation.Failed("[ComputerUse] Bước thất bại."), false);
        }
    }

    private async Task<(bool Approved, bool Cancelled)> TryApproveAsync(ComputerUseApprovalRequest req, CancellationToken ct)
    {
        try { return (await _approvalGate.RequestAsync(req, ct), false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (false, true); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "❌ [ComputerUse] Lỗi ở cổng phê duyệt — từ chối an toàn.");
            return (false, false);
        }
    }

    /// <summary>
    /// Navigation allowlist. Mirrors the executor: an EMPTY allowlist means DENY ALL.
    /// Exact, case-insensitive host match. (The SSRF gate in the executor independently
    /// blocks internal/loopback/metadata targets.)
    /// </summary>
    private bool IsNavigationAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (_options.AllowedHosts.Count == 0) return false;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        return _options.AllowedHosts.Any(a => string.Equals(a, host, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> RenderStep(
        int ordinal, ComputerUseAction action, ComputerUseObservation observation, ComputerUseRequest request)
    {
        yield return "[STEP:" + JsonSerializer.Serialize(new
        {
            ordinal,
            action = action.Type.ToString().ToLowerInvariant(),
            input = action.Describe(),
            observation = ObservationSummary(observation),
        }) + "]";

        if (!string.IsNullOrEmpty(observation.ScreenshotFileId))
        {
            yield return "[FILE:" + JsonSerializer.Serialize(new
            {
                id = observation.ScreenshotFileId,
                name = $"computer-use-step-{ordinal}.png",
                contentType = "image/png",
                size = ScreenshotSize(observation.ScreenshotFileId, request),
            }) + "]";
        }
    }

    private static string ObservationSummary(ComputerUseObservation observation)
    {
        if (observation.IsError) return $"error: {observation.Error}";
        return $"{observation.Title} ({observation.Url}) — {observation.Elements.Count} elements";
    }

    private static string Summarize(ComputerUseAction action, ComputerUseObservation observation)
    {
        var outcome = observation.IsError ? observation.Error : $"{observation.Title} [{observation.Elements.Count} els]";
        return $"{action.Describe()} → {outcome}";
    }

    private static string BuildApprovalDetails(ComputerUseAction action, ComputerUseObservation observation)
    {
        var sb = new StringBuilder();
        sb.Append("Action: ").Append(action.Describe()).Append('\n');
        sb.Append("On page: ").Append(observation.Title).Append(" (").Append(observation.Url).Append(")");
        if (action.Ref is int r)
        {
            var target = observation.Elements.FirstOrDefault(e => e.Ref == r);
            if (target is not null)
                sb.Append("\nTarget: [").Append(target.Ref).Append("] ").Append(target.Role).Append(": ").Append(target.Name);
        }
        var details = sb.ToString();
        return details.Length > 3500 ? details[..3500] : details;
    }

    private string? ResolveScreenshotPath(string? screenshotFileId, ComputerUseRequest request)
    {
        if (string.IsNullOrEmpty(screenshotFileId)) return null;
        var dir = _resources.GetUploadDirectory(request.TenantId, request.UserId);
        var path = Path.Combine(dir, Path.GetFileName(screenshotFileId));
        return File.Exists(path) ? path : null;
    }

    private long ScreenshotSize(string screenshotFileId, ComputerUseRequest request)
    {
        try
        {
            var dir = _resources.GetUploadDirectory(request.TenantId, request.UserId);
            var info = new FileInfo(Path.Combine(dir, Path.GetFileName(screenshotFileId)));
            return info.Exists ? info.Length : 0;
        }
        catch { return 0; }
    }

    private async Task AuditAsync(ComputerUseRequest request, ComputerUseAction action, string status)
    {
        if (_audit is null) return;
        try
        {
            await _audit.RecordAsync(
                request.TenantId, request.UserId, Guid.NewGuid(),
                AuditToolName, action.Describe(), status, TimeSpan.Zero, ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "🧾 [ComputerUse] Ghi nhật ký kiểm toán thất bại (không nghiêm trọng).");
        }
    }

    private static void TrimHistory(List<string> history)
    {
        if (history.Count > MaxHistoryLines)
            history.RemoveRange(0, history.Count - MaxHistoryLines);
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "🧹 [ComputerUse] Không thể dọn thư mục phiên {Dir}.", directory);
        }
    }
}
