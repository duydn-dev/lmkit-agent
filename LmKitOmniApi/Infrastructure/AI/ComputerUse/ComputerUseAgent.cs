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
    private readonly ToolSandboxService _sandbox;
    private readonly AgentToolAuditService? _audit;
    private readonly ILogger<ComputerUseAgent> _logger;

    private const int MaxHistoryLines = 10;
    private const string AuditToolName = "COMPUTER_USE";

    // Logged at most once per process: warns that egress is not network-enforced when the
    // tool is enabled without an operator egress-restricted network (see GAP 2 remarks).
    private static int _egressNotEnforcedWarned;

    public ComputerUseAgent(
        IComputerUseExecutor executor,
        IComputerUseModel model,
        IComputerUseApprovalGate approvalGate,
        IOptions<ComputerUseOptions> options,
        UserResourceAccessService resources,
        ToolSandboxService sandbox,
        ILogger<ComputerUseAgent> logger,
        AgentToolAuditService? audit = null)
    {
        _executor = executor;
        _model = model;
        _approvalGate = approvalGate;
        _options = options.Value;
        _resources = resources;
        _sandbox = sandbox;
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

        WarnIfEgressNotNetworkEnforced();

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

                // Re-validate where we actually LANDED (redirects are not vetted by the executor).
                var startLanding = await RevalidateLandingUrlAsync(observation, sct);
                if (startLanding is not null)
                {
                    _logger.LogWarning("🔒 [ComputerUse] Dừng phiên — trang đích sau điều hướng không hợp lệ: {Reason}", startLanding);
                    await AuditAsync(request, navAction, "refused_offsite");
                    yield return $"[THINKING]: 🔒 {startLanding} — dừng phiên để đảm bảo an toàn.\\n";
                    yield return "Trang hiện tại đã rời khỏi phạm vi cho phép. Tôi dừng lại để đảm bảo an toàn.";
                    yield break;
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

                // Self-correcting decision: on a malformed or un-groundable action the loop
                // re-asks the model with a corrective hint (bounded by GroundingRetries) so it can
                // fix its own grounding BEFORE the fail-closed gates below take over. The model
                // call sits in this non-iterator helper (an iterator can't await inside try/catch).
                var (decided, action, parseError, groundingRetries) =
                    await DecideGroundedAsync(request, observation, history, screenshotPath, sct);
                if (!decided)
                {
                    yield return "[THINKING]: ⚠️ Không lấy được hành động từ mô hình — dừng lại.\\n";
                    yield break;
                }
                if (groundingRetries > 0 && action is not null)
                    yield return $"[THINKING]: ↻ Mô hình tự chỉnh lại hành động sau {groundingRetries} lần nhắc định vị.\\n";
                if (action is null)
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

                // ── FAIL CLOSED: a side-effecting element action (type/click/key) we cannot GROUND
                //    to a resolvable element in the CURRENT observation — coordinate-only, no ref,
                //    or a ref dropped from the observation (stale, or truncated by MaxElements) — is
                //    never executed and never sent to the approval gate. If the agent can't inspect
                //    what it's typing into / clicking, it can't guarantee the target isn't a
                //    credential/CAPTCHA field, so it hands off to a human. (navigate is allowlist +
                //    SSRF gated; read-only scroll/wait/screenshot stay allowed.) ──
                if (RequiresGrounding(action) && !IsGrounded(action, observation))
                {
                    _logger.LogWarning("🛑 [ComputerUse] Từ chối hành động không định vị được phần tử: {Action}", action.Describe());
                    await AuditAsync(request, action, "refused_ungroundable");
                    yield return "[THINKING]: 🛑 Không xác định được phần tử mục tiêu cho hành động này — chuyển giao cho con người.\\n";
                    yield return "Tôi không thể tự thực hiện thao tác này vì không xác định được chính xác phần tử mục tiêu trong trang "
                                 + "(có thể là ô nhập thông tin đăng nhập/thanh toán hoặc CAPTCHA). "
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

                // ── Post-navigation re-validation: a link click / JS nav / redirect can leave the
                //    page on a host the initial allowlist + SSRF check never saw. Re-vet where we
                //    ended up and STOP the session fail-closed if it is off-allowlist or resolves to
                //    a private/loopback/metadata host — do NOT keep driving an off-allowlist page. ──
                var landing = await RevalidateLandingUrlAsync(observation, sct);
                if (landing is not null)
                {
                    _logger.LogWarning("🔒 [ComputerUse] Dừng phiên — trang rời allowlist/SSRF sau bước: {Reason}", landing);
                    await AuditAsync(request, action, "refused_offsite");
                    yield return $"[THINKING]: 🔒 {landing} — dừng phiên để đảm bảo an toàn.\\n";
                    yield return "Trang hiện tại đã rời khỏi phạm vi cho phép. Tôi dừng lại để đảm bảo an toàn.";
                    yield break;
                }
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

    /// <summary>
    /// Grounding-robustness layer. Asks the model for the next action and, when the reply is
    /// malformed OR targets a <c>ref</c> that is not in the CURRENT observation (a hallucinated
    /// or stale element), re-asks with a corrective hint up to
    /// <see cref="ComputerUseOptions.GroundingRetries"/> times, giving the model a chance to fix
    /// its own grounding. Returns the first well-formed + grounded action; once the budget is
    /// spent it returns the last attempt AS-IS (Action=null when still unparseable) so the
    /// caller's existing fail-closed gates (credential/CAPTCHA refusal, un-groundable handoff,
    /// navigation allowlist) remain the final authority — this layer only reduces how often a
    /// capable model gets needlessly handed off. A regular async method (not an iterator) so the
    /// model call can sit inside try/catch. <c>Retries</c> is how many corrective re-asks happened.
    /// </summary>
    private async Task<(bool Decided, ComputerUseAction? Action, string? ParseError, int Retries)> DecideGroundedAsync(
        ComputerUseRequest request, ComputerUseObservation observation,
        IReadOnlyList<string> history, string? screenshotPath, CancellationToken ct)
    {
        string? correction = null;
        ComputerUseAction? lastAction = null;
        string? lastParseError = null;
        var maxAttempts = Math.Max(0, _options.GroundingRetries) + 1;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            // On a retry, append the correction as a TRANSIENT extra history line so the model sees
            // exactly what was wrong; the caller's persistent `history` is never mutated here.
            IReadOnlyList<string> effectiveHistory = correction is null
                ? history
                : new List<string>(history) { "Nhắc sửa: " + correction };
            var prompt = new ComputerUsePrompt(request.TaskGoal, SystemPrompt, observation, effectiveHistory, screenshotPath);

            var (raw, decided) = await TryDecideAsync(prompt, ct);
            if (!decided) return (false, null, null, attempt);

            if (!ComputerUseActionParser.TryParse(raw, out var action, out var parseError) || action is null)
            {
                lastAction = null;
                lastParseError = parseError;
                correction = $"Hành động vừa rồi không hợp lệ ({parseError}). Chỉ trả về ĐÚNG MỘT JSON theo schema, không kèm văn bản.";
                continue;
            }

            lastAction = action;
            lastParseError = null;

            // Needs an element target but the ref isn't in this observation → let it self-correct
            // with the list of valid refs before the step's fail-closed handoff takes over.
            if (RequiresGrounding(action) && !IsGrounded(action, observation))
            {
                correction = BuildGroundingHint(observation);
                continue;
            }

            return (true, action, null, attempt); // well-formed and (if needed) grounded
        }

        // Budget spent: hand back the last attempt so the loop's fail-closed gates handle it.
        return (true, lastAction, lastParseError, maxAttempts - 1);
    }

    /// <summary>Corrective hint listing the valid element refs in the current observation, so the
    /// model re-picks a REAL target (or asks a human) instead of a hallucinated/stale/coordinate ref.</summary>
    private string BuildGroundingHint(ComputerUseObservation observation)
    {
        var refs = observation.Elements
            .Take(Math.Max(1, _options.MaxElements))
            .Select(e => string.IsNullOrWhiteSpace(e.Name) ? $"{e.Ref}:{e.Role}" : $"{e.Ref}:{e.Role} \"{e.Name}\"");
        var list = string.Join(", ", refs);
        return string.IsNullOrEmpty(list)
            ? "Trang hiện không có phần tử tương tác nào để chọn 'ref'. Nếu cần thao tác hãy trả {\"action\":\"ask\",\"question\":\"...\"} để nhờ con người; hoặc dùng 'scroll'/'navigate' hợp lệ."
            : $"'ref' bạn chọn không có trong trang hiện tại. Chỉ dùng 'ref' từ danh sách phần tử: [{list}]. "
              + "Nếu không có phần tử phù hợp, trả {\"action\":\"ask\",\"question\":\"...\"} thay vì đoán toạ độ.";
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

    /// <summary>
    /// Only element-targeted, side-effecting actions must be grounded to an inspectable
    /// element: <c>type</c>, <c>click</c>, <c>key</c>. <c>navigate</c> is host-gated
    /// (allowlist + SSRF) instead, and read-only ops (scroll / wait / screenshot) plus
    /// terminals (done / ask) need no target.
    /// </summary>
    private static bool RequiresGrounding(ComputerUseAction action) => action.Type
        is ComputerUseActionType.Type or ComputerUseActionType.Click or ComputerUseActionType.Key;

    /// <summary>
    /// An action is GROUNDED only when it carries a <see cref="ComputerUseAction.Ref"/> that
    /// resolves to an element in the CURRENT observation. Coordinate-only actions, actions
    /// with no ref (e.g. a bare <c>key</c> press — which would type into whatever is focused,
    /// an element we cannot inspect), and refs absent from the current observation (stale, or
    /// dropped by <see cref="ComputerUseOptions.MaxElements"/> truncation) are NOT grounded.
    /// </summary>
    private static bool IsGrounded(ComputerUseAction action, ComputerUseObservation observation)
    {
        if (action.Ref is not int refId) return false;
        foreach (var element in observation.Elements)
            if (element.Ref == refId) return true;
        return false;
    }

    /// <summary>
    /// Re-vets where the browser actually ENDED UP after a step (the observation's reported
    /// url) against the navigation allowlist AND the SSRF gate. Returns a non-null reason to
    /// STOP the session when the landing page is off-allowlist or resolves to a
    /// private/loopback/metadata host; returns null to continue. Only http/https landings are
    /// checked — <c>about:blank</c>, <c>data:</c>, etc. carry no egress host. Fails CLOSED:
    /// an unexpected validation error also stops the session.
    /// </summary>
    private async Task<string?> RevalidateLandingUrlAsync(ComputerUseObservation observation, CancellationToken ct)
    {
        var url = observation.Url;
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        if (!IsNavigationAllowed(url))
            return $"Trang đích '{uri.Host}' nằm ngoài danh sách máy chủ được phép";

        try
        {
            var ssrf = await _sandbox.ValidateUrlAsync(url, ct);
            if (!ssrf.IsAllowed)
                return $"Trang đích '{uri.Host}' bị cổng SSRF từ chối: {ssrf.DenialReason}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null; // cancellation is handled by the loop's own checks; no spurious stop
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "🔒 [ComputerUse] Lỗi khi thẩm định URL đích — dừng an toàn.");
            return $"Không thẩm định được trang đích '{uri.Host}'"; // fail closed
        }
        return null;
    }

    /// <summary>
    /// One-time WARNING (per process) that egress is not enforced at the network layer when
    /// the tool is enabled without <see cref="ComputerUseOptions.NetworkName"/>. In that mode
    /// the host allowlist + per-step landing re-validation are the ONLY host constraints; an
    /// operator egress-restricted network is still the recommended backstop for
    /// subresources/websockets the host cannot see. Warns only — never hard-fails.
    /// </summary>
    private void WarnIfEgressNotNetworkEnforced()
    {
        if (!string.IsNullOrWhiteSpace(_options.NetworkName)) return;
        if (Interlocked.Exchange(ref _egressNotEnforcedWarned, 1) != 0) return;
        _logger.LogWarning(
            "⚠️ [ComputerUse] ComputerUse:NetworkName trống — egress KHÔNG được cưỡng chế ở tầng mạng. "
            + "Chỉ dựa vào allowlist máy chủ + thẩm định URL đích sau mỗi bước; hãy cấu hình một mạng "
            + "hạn chế egress (NetworkName) để phòng thủ đầy đủ trước redirect/subresource/websocket.");
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
