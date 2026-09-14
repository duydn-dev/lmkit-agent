using MediatR;

namespace LmKitOmniApi.Application.AgentRuns.Commands;

public enum CancelAgentRunOutcome
{
    /// <summary>Không có run như vậy trong phạm vi tenant+user của người gọi (→ 404).</summary>
    NotFound,

    /// <summary>Đã hủy: run đóng Failed với thông điệp hủy, approval treo bị Cancelled (→ 204).</summary>
    Cancelled,

    /// <summary>Run đã ở trạng thái kết thúc — không có gì để hủy (→ 409).</summary>
    AlreadyFinished,

    /// <summary>
    /// Run đang STREAM trực tiếp trong một tiến trình (Running, không có bookkeeping
    /// resume, chưa quá ngưỡng mồ côi) — server không với tới CancellationToken của
    /// stream đó; ngắt kết nối stream là cách dừng (→ 409 kèm hướng dẫn).
    /// </summary>
    StreamingActive
}

/// <summary>
/// Hủy một agent run theo yêu cầu người dùng. Hủy được các trạng thái ĐANG ĐỖ:
/// chờ phê duyệt (AwaitingApproval — approval treo bị đánh dấu Cancelled để biến
/// khỏi hộp phê duyệt), chờ lượt resume (Running + ResumeState=Pending), lượt
/// resume có lease đã hết hạn, và run Running mồ côi (stream chết không kịp
/// finalize) quá <see cref="Handlers.CancelAgentRunCommandHandler.OrphanedRunningAge"/>.
/// Run đang stream sống trả về <see cref="CancelAgentRunOutcome.StreamingActive"/>.
/// </summary>
public sealed class CancelAgentRunCommand : IRequest<CancelAgentRunOutcome>
{
    public Guid RunId { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
}
