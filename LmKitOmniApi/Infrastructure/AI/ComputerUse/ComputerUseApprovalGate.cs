using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.ComputerUse;

/// <summary>
/// Default <see cref="IComputerUseApprovalGate"/> that reuses the existing approval
/// substrate. For each side-effecting action it:
///  1. ensures a hidden computer-use <see cref="ChatSession"/> exists (the HITL substrate,
///     excluded from the chat list exactly like an agent run) so the approval row's FK is
///     valid, then
///  2. inserts a <see cref="TaskApproval"/> (status Pending, owner-scoped, payload
///     encrypted by <see cref="TaskApprovalPayloadProtector"/>) — which surfaces in the
///     user's existing pending-approvals list, and
///  3. waits, polling the row, for a human decision.
///
/// It FAILS CLOSED: it returns true ONLY when the row reaches an explicit approved state
/// (<c>Approved</c>/<c>Completed</c>); a rejected/failed state, the timeout
/// (<see cref="ComputerUseOptions.ApprovalTimeoutSeconds"/>), cancellation, or any error
/// all return false so the action is NOT executed.
///
/// LIVE-ONLY: the waiting + database interaction are exercised in the running stack, not
/// in CI (the loop's own tests inject a scripted approver through the
/// <see cref="IComputerUseApprovalGate"/> seam). Computer-use approvals should be resolved
/// through the dedicated endpoints on <c>ComputerUseController</c>
/// (<c>/api/agent/computer-use/approvals/{id}/approve|reject</c>), which set the status
/// this gate waits on WITHOUT routing the action through the generic tool dispatcher
/// (the action executes inside the loop, not via a dispatcher tool).
/// </summary>
public sealed class ComputerUseApprovalGate : IComputerUseApprovalGate
{
    // Terminal statuses the gate recognises. "Approved" is the go-ahead the dedicated
    // resolve endpoint sets; "Completed" is accepted for forward-compatibility.
    private static readonly HashSet<string> ApprovedStatuses =
        new(new[] { "Approved", "Completed" }, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DeniedStatuses =
        new(new[] { "Rejected", "Failed", "Cancelled", "Canceled", "Denied" }, StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TaskApprovalPayloadProtector _payloadProtector;
    private readonly ComputerUseOptions _options;
    private readonly ILogger<ComputerUseApprovalGate> _logger;

    public ComputerUseApprovalGate(
        IServiceScopeFactory scopeFactory,
        TaskApprovalPayloadProtector payloadProtector,
        IOptions<ComputerUseOptions> options,
        ILogger<ComputerUseApprovalGate> logger)
    {
        _scopeFactory = scopeFactory;
        _payloadProtector = payloadProtector;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> RequestAsync(ComputerUseApprovalRequest request, CancellationToken ct = default)
    {
        try
        {
            await CreatePendingApprovalAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Could not even record the request — fail closed.
            _logger.LogError(ex, "❌ [ComputerUse] Không tạo được yêu cầu phê duyệt {ApprovalId}.", request.ApprovalId);
            return false;
        }

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, _options.ApprovalTimeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false; // client went away → treat as not-approved
            }

            var status = await ReadStatusAsync(request, ct);
            if (status is null) continue;
            if (ApprovedStatuses.Contains(status))
            {
                _logger.LogInformation("✅ [ComputerUse] Hành động được phê duyệt (approval {ApprovalId}).", request.ApprovalId);
                return true;
            }
            if (DeniedStatuses.Contains(status))
            {
                _logger.LogInformation("🚫 [ComputerUse] Hành động bị từ chối (approval {ApprovalId}).", request.ApprovalId);
                return false;
            }
        }

        _logger.LogWarning("⏱️ [ComputerUse] Phê duyệt {ApprovalId} hết thời gian chờ — từ chối an toàn.", request.ApprovalId);
        return false;
    }

    private async Task CreatePendingApprovalAsync(ComputerUseApprovalRequest request, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();

        // Ensure the hidden computer-use session exists so the approval FK is valid.
        var sessionExists = await db.ChatSessions
            .AsNoTracking()
            .AnyAsync(s => s.Id == request.SessionId, ct);
        if (!sessionExists)
        {
            db.ChatSessions.Add(new ChatSession
            {
                Id = request.SessionId,
                TenantId = request.TenantId,
                UserId = request.UserId,
                Title = "Computer-use",
                IsAgentRun = true, // hidden substrate — never shows in the chat list
            });
        }

        db.TaskApprovals.Add(new TaskApproval
        {
            Id = request.ApprovalId,
            TenantId = request.TenantId,
            UserId = request.UserId,
            ChatSessionId = request.SessionId,
            ActionName = "COMPUTER_USE",
            ParametersJson = _payloadProtector.Protect(request.Details),
            Status = "Pending",
        });

        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "⚠️ [ComputerUse] Chờ phê duyệt hành động '{Summary}' (approval {ApprovalId}).",
            request.ActionSummary, request.ApprovalId);
    }

    private async Task<string?> ReadStatusAsync(ComputerUseApprovalRequest request, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
            return await db.TaskApprovals
                .AsNoTracking()
                .Where(t => t.Id == request.ApprovalId
                    && t.TenantId == request.TenantId
                    && t.UserId == request.UserId)
                .Select(t => t.Status)
                .FirstOrDefaultAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "🔁 [ComputerUse] Lỗi khi đọc trạng thái phê duyệt; sẽ thử lại.");
            return null;
        }
    }
}
