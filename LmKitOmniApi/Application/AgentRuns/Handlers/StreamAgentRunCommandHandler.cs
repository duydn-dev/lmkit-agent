using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
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
    // Strips the orchestrator's status/step markers so the stored Result is clean prose.
    private static readonly Regex MarkerRegex = new(
        @"\[(?:THINKING|REASONING|WEB_SEARCH|Agent invoked|STEP|FILE|HITL_APPROVAL_REQUIRED|AGENT_RUN|RESEARCH_SAVED)[:\]][^\n\r]*?(?:\][\n\r]*|(?=\[)|$)",
        RegexOptions.Compiled);

    private readonly IAgentOrchestrator _orchestrator;
    private readonly HermesDbContext _dbContext;
    private readonly LmModelManager _modelManager;

    public StreamAgentRunCommandHandler(
        IAgentOrchestrator orchestrator, HermesDbContext dbContext, LmModelManager modelManager)
    {
        _orchestrator = orchestrator;
        _dbContext = dbContext;
        _modelManager = modelManager;
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
            Status = "Running"
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
        var model = await _modelManager.GetChatModelAsync(ct: cancellationToken);
        var history = new ChatHistory(model);
        var completed = false;
        var awaitingApproval = false;

        try
        {
            await foreach (var text in _orchestrator.StreamProcessQueryAsync(
                request.TenantId, session.Id, request.UserId, agentRole, goal, history, options: null,
                cancellationToken, steps))
            {
                contentBuilder.Append(text);
                if (text.StartsWith("[HITL_APPROVAL_REQUIRED:", StringComparison.Ordinal)) awaitingApproval = true;
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

        var result = MarkerRegex.Replace(rawContent, string.Empty).Trim();
        run.Result = string.IsNullOrWhiteSpace(result) ? null : result;
        run.Status = awaitingApproval ? "AwaitingApproval" : completed ? "Completed" : "Failed";
        if (!completed && !awaitingApproval) run.Error = "Thực thi bị dừng hoặc thất bại.";
        run.CompletedAtUtc = awaitingApproval ? null : DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(CancellationToken.None);
    }
}
