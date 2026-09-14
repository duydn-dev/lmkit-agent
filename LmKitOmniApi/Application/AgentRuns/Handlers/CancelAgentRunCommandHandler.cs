using LmKitOmniApi.Application.AgentRuns.Commands;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.AgentRuns.Handlers;

public sealed class CancelAgentRunCommandHandler : IRequestHandler<CancelAgentRunCommand, CancelAgentRunOutcome>
{
    /// <summary>
    /// Run Running KHÔNG có bookkeeping resume già hơn ngưỡng này được coi là mồ côi
    /// (tiến trình stream chết trước khi finally kịp finalize) và hủy được. Một run
    /// stream sống không thể già đến vậy: vòng ReAct bị chặn ở 5 lượt và nhánh lịch
    /// bị cắt ở 8 phút.
    /// </summary>
    public static readonly TimeSpan OrphanedRunningAge = TimeSpan.FromMinutes(15);

    public const string CancelledByUserMessage = "Đã hủy theo yêu cầu của người dùng.";

    private readonly HermesDbContext _dbContext;
    private readonly ILogger<CancelAgentRunCommandHandler> _logger;

    public CancelAgentRunCommandHandler(HermesDbContext dbContext, ILogger<CancelAgentRunCommandHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<CancelAgentRunOutcome> Handle(CancelAgentRunCommand request, CancellationToken cancellationToken)
    {
        var run = await _dbContext.AgentRuns.AsNoTracking()
            .Where(r => r.Id == request.RunId && r.TenantId == request.TenantId && r.UserId == request.UserId)
            .Select(r => new { r.Id, r.Status, r.CompletedAtUtc, r.ChatSessionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null) return CancelAgentRunOutcome.NotFound;

        if (run.CompletedAtUtc is not null) return CancelAgentRunOutcome.AlreadyFinished;

        var now = DateTime.UtcNow;
        var orphanCutoff = now - OrphanedRunningAge;

        // MỘT câu UPDATE có điều kiện — điều kiện đủ tư cách hủy nằm ngay trong WHERE
        // nên đua với resume-worker/reconciler là an toàn: bên nào thắng bên đó viết,
        // updated==0 nghĩa là trạng thái vừa đổi dưới chân và ta đọc lại để trả lời đúng.
        // Không hủy lượt resume ĐANG được một worker cầm lease còn hạn: pass đó sẽ tự
        // finalize; hủy đè lên nó chỉ tạo kết quả chồng nhau.
        var updated = await _dbContext.AgentRuns
            .Where(r => r.Id == request.RunId
                && r.TenantId == request.TenantId
                && r.UserId == request.UserId
                && r.CompletedAtUtc == null
                && (r.Status == AgentRunStatuses.AwaitingApproval
                    || (r.Status == AgentRunStatuses.Running && r.ResumeState == AgentRunResumeStates.Pending)
                    || (r.Status == AgentRunStatuses.Running
                        && r.ResumeState == AgentRunResumeStates.Claimed
                        && r.ResumeLeaseUntilUtc != null
                        && r.ResumeLeaseUntilUtc < now)
                    || (r.Status == AgentRunStatuses.Running
                        && r.ResumeState == null
                        && r.CreatedAtUtc < orphanCutoff)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Status, AgentRunStatuses.Failed)
                .SetProperty(r => r.Error, CancelledByUserMessage)
                .SetProperty(r => r.CompletedAtUtc, now)
                .SetProperty(r => r.ResumeState, (string?)null)
                .SetProperty(r => r.ResumeLeaseUntilUtc, (DateTime?)null),
                cancellationToken);

        if (updated == 0)
        {
            // Đọc lại để phân biệt "vừa kết thúc" với "đang stream sống".
            var current = await _dbContext.AgentRuns.AsNoTracking()
                .Where(r => r.Id == request.RunId)
                .Select(r => new { r.CompletedAtUtc })
                .FirstOrDefaultAsync(cancellationToken);
            return current?.CompletedAtUtc is not null
                ? CancelAgentRunOutcome.AlreadyFinished
                : CancelAgentRunOutcome.StreamingActive;
        }

        // Approval treo của phiên run này chuyển Cancelled để rời hộp phê duyệt —
        // cùng từ vựng computer-use đã dùng cho "người yêu cầu đã bỏ đi".
        var cancelledApprovals = await _dbContext.TaskApprovals
            .Where(approval => approval.ChatSessionId == run.ChatSessionId && approval.Status == "Pending")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.Status, TaskApproval.CancelledStatus)
                .SetProperty(a => a.ResolvedAtUtc, now),
                cancellationToken);

        _logger.LogInformation(
            "🛑 Agent run {RunId} cancelled by user (status was {Status}; {ApprovalCount} pending approval(s) cancelled).",
            request.RunId, run.Status, cancelledApprovals);
        return CancelAgentRunOutcome.Cancelled;
    }
}
