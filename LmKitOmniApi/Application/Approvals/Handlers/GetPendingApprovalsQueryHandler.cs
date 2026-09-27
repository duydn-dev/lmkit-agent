using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.AgentRuns;
using LmKitOmniApi.Application.Approvals.Queries;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;

namespace LmKitOmniApi.Application.Approvals.Handlers;

public class GetPendingApprovalsQueryHandler : IRequestHandler<GetPendingApprovalsQuery, List<PendingApprovalDto>>
{
    private const int MaxDetailsChars = 4000;

    private readonly HermesDbContext _dbContext;
    private readonly TaskApprovalPayloadProtector _payloadProtector;

    public GetPendingApprovalsQueryHandler(HermesDbContext dbContext, TaskApprovalPayloadProtector payloadProtector)
    {
        _dbContext = dbContext;
        _payloadProtector = payloadProtector;
    }

    public async Task<List<PendingApprovalDto>> Handle(GetPendingApprovalsQuery request, CancellationToken cancellationToken)
    {
        // Defence in depth on the deadline: an overdue row is filtered out here even
        // though the sweeper normally flips its Status to Expired first. The sweeper can
        // be disabled, behind, or down, and this list is what a human picks from — it must
        // never offer an action the approve endpoint would refuse anyway. It is also what
        // stops the list growing without bound from approvals nobody ever answered.
        var now = DateTime.UtcNow;
        var rows = await _dbContext.TaskApprovals
            .Where(t => t.TenantId == request.TenantId
                && t.UserId == request.UserId
                && t.Status == "Pending"
                && t.ExpiresAtUtc > now)
            .OrderByDescending(t => t.CreatedAtUtc)
            .Select(t => new
            {
                t.Id,
                t.ActionName,
                t.ParametersJson,
                t.CreatedAtUtc,
                t.ExpiresAtUtc,
                t.ChatSessionId,
                // LEFT JOIN through the navigation. The FK is DeleteBehavior.Cascade, so a null
                // session means the row is on its way out; treat that as "not a conversation"
                // rather than assuming it is one.
                IsChatSession = t.ChatSession != null
                    && !t.ChatSession.IsAgentRun
                    && !t.ChatSession.IsEphemeral,
                ChatSessionTitle = t.ChatSession != null ? t.ChatSession.Title : null
            })
            .ToListAsync(cancellationToken);

        // One batched lookup pairs every gated approval with its agent run. The run is the
        // approval's own chat session's CURRENT run: still parked (Running / AwaitingApproval)
        // when it is, else the most recent one — so the detail link follows the run forward
        // when a resumed run gates again on a new approval. Rows are fetched newest-first and
        // grouped in memory because EF cannot translate OrderBy inside GroupBy.
        var sessionIds = rows.Select(t => t.ChatSessionId).Distinct().ToList();
        Dictionary<Guid, Guid> runsBySession;
        if (sessionIds.Count == 0)
        {
            runsBySession = [];
        }
        else
        {
            var runs = await _dbContext.AgentRuns
                .AsNoTracking()
                .Where(r => sessionIds.Contains(r.ChatSessionId))
                .OrderByDescending(r => r.CreatedAtUtc)
                .Select(r => new { r.Id, r.ChatSessionId, r.Status })
                .ToListAsync(cancellationToken);

            runsBySession = runs
                .GroupBy(r => r.ChatSessionId)
                .ToDictionary(
                    g => g.Key,
                    g => g.FirstOrDefault(r => r.Status == AgentRunStatuses.Running || r.Status == AgentRunStatuses.AwaitingApproval)?.Id
                        ?? g.First().Id);
        }

        return rows.Select(t => new PendingApprovalDto
        {
            Id = t.Id,
            ActionName = t.ActionName,
            Details = Describe(t.ParametersJson),
            CreatedAtUtc = t.CreatedAtUtc,
            ExpiresAtUtc = t.ExpiresAtUtc,
            ChatSessionId = t.ChatSessionId,
            IsChatSession = t.IsChatSession,
            ChatSessionTitle = t.IsChatSession ? (t.ChatSessionTitle ?? string.Empty) : string.Empty,
            AgentRunId = runsBySession.TryGetValue(t.ChatSessionId, out var agentRunId) ? agentRunId : null
        }).ToList();
    }

    private string Describe(string parametersJson)
    {
        if (string.IsNullOrEmpty(parametersJson)) return string.Empty;
        string decrypted;
        try { decrypted = _payloadProtector.Unprotect(parametersJson); }
        catch { return string.Empty; } // never surface a decrypt failure as content
        return decrypted.Length > MaxDetailsChars ? decrypted[..MaxDetailsChars] + "…" : decrypted;
    }
}
