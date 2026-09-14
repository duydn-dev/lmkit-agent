using System.Runtime.CompilerServices;
using System.Text;
using LmKitOmniApi.Application.AgentRuns.Commands;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Services;
using LMKit.TextGeneration.Chat;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.AgentRuns.Handlers;

/// <summary>
/// Drives a goal-oriented autonomous agent run on the shared ReAct orchestrator,
/// persisting the run, each tool step, and the final result. Streams the same
/// marker channel as chat (plus [STEP:] and a leading [AGENT_RUN:{id}]). The
/// hidden IsAgentRun chat session is the HITL/approval substrate and never shows
/// in the chat list.
/// </summary>
public sealed class StreamAgentRunCommandHandler : IStreamRequestHandler<StreamAgentRunCommand, string>
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly HermesDbContext _dbContext;
    private readonly IAgentRunHistoryFactory _historyFactory;

    public StreamAgentRunCommandHandler(
        IAgentOrchestrator orchestrator, HermesDbContext dbContext, IAgentRunHistoryFactory historyFactory)
    {
        _orchestrator = orchestrator;
        _dbContext = dbContext;
        _historyFactory = historyFactory;
    }

    public async IAsyncEnumerable<string> Handle(
        StreamAgentRunCommand request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var storedRole = await _dbContext.Users
            .Where(user => user.Id == request.UserId && user.TenantId == request.TenantId && user.IsActive)
            .Select(user => user.Role)
            .FirstOrDefaultAsync(cancellationToken);
        if (storedRole is null) throw new UnauthorizedAccessException("User is inactive or access was revoked.");
        var agentRole = storedRole.Equals("Admin", StringComparison.OrdinalIgnoreCase) ? "Admin" : "User";

        var goal = request.Goal.Trim();

        // Persona (tùy chọn): cùng phép resolve + cùng phép hợp thành options với chat
        // (StreamChatCommandHandler) — tenant-scoped, chủ sở hữu hoặc agent chia sẻ.
        // Không tìm thấy → chạy không persona: soft reference đúng nghĩa.
        Application.Abstractions.AgentRequestOptions? options = null;
        if (request.CustomAgentId is { } customAgentId)
        {
            var customAgent = await _dbContext.CustomAgents
                .AsNoTracking()
                .FirstOrDefaultAsync(agent => agent.Id == customAgentId
                    && agent.TenantId == request.TenantId
                    && (agent.OwnerUserId == request.UserId || agent.IsSharedWithTenant),
                    cancellationToken);
            if (customAgent is not null)
            {
                var allowedTools = Application.CustomAgents.CustomAgentRules.ParseToolsCsv(customAgent.AllowedToolsCsv);
                options = new Application.Abstractions.AgentRequestOptions
                {
                    AllowWebSearch = allowedTools is null
                        || allowedTools.Contains("SearchWeb", StringComparer.OrdinalIgnoreCase),
                    PersonaPrompt = customAgent.PersonaPrompt,
                    AllowedTools = allowedTools,
                    KnowledgeDocumentIds = Application.CustomAgents.CustomAgentRules.ParseDocumentIdsCsv(customAgent.KnowledgeDocumentIdsCsv),
                    LoraAdapterId = customAgent.LoraAdapterId
                };
            }
        }

        // Hidden session = HITL/approval substrate; excluded from the chat list.
        var session = new ChatSession
        {
            TenantId = request.TenantId,
            UserId = request.UserId,
            Title = goal.Length > 200 ? goal[..200] : goal,
            IsAgentRun = true
        };
        _dbContext.ChatSessions.Add(session);

        var run = new AgentRun
        {
            TenantId = request.TenantId,
            UserId = request.UserId,
            ChatSessionId = session.Id,
            Goal = goal.Length > 4000 ? goal[..4000] : goal,
            Status = AgentRunStatuses.Running
        };
        _dbContext.AgentRuns.Add(run);
        await _dbContext.SaveChangesAsync(cancellationToken);
        request.RunId = run.Id;

        // First event: the run id, so the client can deep-link / poll the detail.
        yield return $"[AGENT_RUN:{run.Id}]";

        var steps = new List<AgentRunStepData>();
        var contentBuilder = new StringBuilder();
        // A fresh run carries no prior turns; the history object still needs the
        // chat model (same construction the chat handler uses).
        // Through the same seam the resume worker uses, so this handler can be driven
        // without weights (ChatHistory tolerates a null model) and both passes build their
        // history the same way.
        var history = await _historyFactory.CreateAsync(cancellationToken);
        var completed = false;
        var awaitingApproval = false;

        try
        {
            await foreach (var text in _orchestrator.StreamProcessQueryAsync(
                request.TenantId, session.Id, request.UserId, agentRole, goal, history, options,
                cancellationToken, steps))
            {
                contentBuilder.Append(text);
                if (text.StartsWith(AgentRunMarkers.ApprovalRequired, StringComparison.Ordinal)) awaitingApproval = true;
                yield return text;
            }
            completed = true;
        }
        finally
        {
            // Persist steps + outcome even on cancellation/error (None token: the
            // request token may already be canceled, same rationale as the chat handler).
            await FinalizeAsync(run, steps, contentBuilder.ToString(), completed, awaitingApproval);
        }
    }

    private async Task FinalizeAsync(
        AgentRun run, IReadOnlyList<AgentRunStepData> steps, string rawContent, bool completed, bool awaitingApproval)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            _dbContext.AgentRunSteps.Add(new AgentRunStep
            {
                AgentRunId = run.Id,
                Ordinal = i + 1,
                Action = step.Action.Length > 64 ? step.Action[..64] : step.Action,
                Input = step.Input,
                Observation = step.Observation
            });
        }

        var result = AgentRunMarkers.StripMarkers(rawContent);
        run.Result = string.IsNullOrWhiteSpace(result) ? null : result;
        // AwaitingApproval is the one non-terminal outcome: CompletedAtUtc stays null
        // until the human resolves the gating approval, at which point
        // AgentRunApprovalReconciler either hands the run back to the ReAct loop
        // (AgentRunResumeService) or closes it. Without that reconciliation this state
        // was permanent — see AgentRunStatuses.
        run.Status = awaitingApproval ? AgentRunStatuses.AwaitingApproval
            : completed ? AgentRunStatuses.Completed
            : AgentRunStatuses.Failed;
        if (!completed && !awaitingApproval)
        {
            // The one stop that has a specific reason on record: the inference queue turned
            // the run away, and the orchestrator left that reason as the last step.
            var refusal = steps.Count > 0 && steps[^1].Action == AgentRunStepData.AdmissionRefusedAction
                ? steps[^1].Observation
                : null;
            run.Error = string.IsNullOrWhiteSpace(refusal) ? "Thực thi bị dừng hoặc thất bại." : refusal;
        }
        run.CompletedAtUtc = awaitingApproval ? null : DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(CancellationToken.None);
    }
}
