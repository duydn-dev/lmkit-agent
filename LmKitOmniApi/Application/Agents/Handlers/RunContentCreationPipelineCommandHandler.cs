using MediatR;
using LMKit.Agents;
using LMKit.Agents.Orchestration;
using LmKitOmniApi.Application.Agents.Commands;
using LmKitOmniApi.Services;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.AI.Tools;

namespace LmKitOmniApi.Application.Agents.Handlers;

public class RunContentCreationPipelineCommandHandler : IRequestHandler<RunContentCreationPipelineCommand, RunContentCreationPipelineResult>
{
    private readonly LmModelManager _modelManager;
    private readonly IRagPipelineService _ragService;
    private readonly IWebSearchService _webSearch;
    private readonly AgentToolGateway _toolGateway;

    public RunContentCreationPipelineCommandHandler(
        LmModelManager modelManager,
        IRagPipelineService ragService,
        IWebSearchService webSearch,
        AgentToolGateway toolGateway)
    {
        _modelManager = modelManager;
        _ragService = ragService;
        _webSearch = webSearch;
        _toolGateway = toolGateway;
    }

    public async Task<RunContentCreationPipelineResult> Handle(RunContentCreationPipelineCommand request, CancellationToken cancellationToken)
    {
        var model = await _modelManager.GetChatModelAsync();

        var outlinerAgent = Agent.CreateBuilder(model)
            .WithPersona(@"Outliner - You are an expert Content Outliner. Your job is to analyze a topic and create a well-structured outline.
Include: A compelling title, Introduction, 3-5 main sections, Key points, Conclusion.")
            .WithPlanning(PlanningStrategy.None)
            .Build();

        var writerAgent = Agent.CreateBuilder(model)
            .WithPersona(@"Writer - You are a professional Content Writer. Expand the outline into engaging, well-written prose. Aim for 400-600 words.")
            .WithPlanning(PlanningStrategy.None)
            .Build();

        var editorAgent = Agent.CreateBuilder(model)
            .WithPersona(@"Editor - You are a meticulous Editor. Refine and polish written content (Grammar, readability, flow). Output only the polished text.")
            .WithPlanning(PlanningStrategy.None)
            .Build();

        var factCheckerAgent = Agent.CreateBuilder(model)
            .WithPersona(@"FactChecker - Verify factual claims against the supplied internal knowledge and current web evidence. Cite the evidence returned by tools, clearly label uncertainty, and never invent a source. Output final content.")
            .WithPlanning(PlanningStrategy.ReAct)
            .WithTools(tools =>
            {
                tools.Register(new DelegatedActionTool(
                    "query_internal_knowledge",
                    "Retrieve tenant-scoped internal evidence for fact checking.",
                    async (query, ct) =>
                    {
                        var execution = await _toolGateway.ExecuteReadOnlyAsync(
                            request.TenantId,
                            request.UserId,
                            request.UserRole,
                            "QueryKnowledgeBase",
                            query,
                            _ => _ragService.QueryKnowledgeBaseAsync(request.TenantId, request.UserId, query, topK: 5),
                            ct);
                        if (!execution.IsSuccess) throw new InvalidOperationException(execution.ErrorMessage);
                        return execution.Output;
                    }));
                tools.Register(new DelegatedActionTool(
                    "search_current_web",
                    "Search current web evidence and return source URLs.",
                    async (query, ct) =>
                    {
                        var execution = await _toolGateway.ExecuteReadOnlyAsync(
                            request.TenantId,
                            request.UserId,
                            request.UserRole,
                            "SearchWeb",
                            query,
                            token => _webSearch.SearchWebAsync(query, count: 5, token),
                            ct);
                        if (!execution.IsSuccess) throw new InvalidOperationException(execution.ErrorMessage);
                        return execution.Output;
                    }));
            })
            .WithMaxIterations(6)
            .Build();

        var pipeline = new PipelineOrchestrator()
            .AddStage("Outliner", outlinerAgent)
            .AddStage("Writer", writerAgent)
            .AddStage("Editor", editorAgent)
            .AddStage("FactChecker", factCheckerAgent);

        var pipelineResult = await pipeline.ExecuteAsync(
            $"Create content about: {request.Topic}",
            cancellationToken);
        if (!pipelineResult.Success)
        {
            var stageErrors = string.Join("; ", pipelineResult.AgentResults
                .Where(stage => !stage.IsSuccess)
                .Select(stage => stage.Error?.Message)
                .Where(message => !string.IsNullOrWhiteSpace(message)));
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stageErrors) ? "Content pipeline failed." : stageErrors);
        }

        var result = new RunContentCreationPipelineResult
        {
            FinalContent = pipelineResult.Content ?? string.Empty,
            TotalDurationSeconds = pipelineResult.Duration.TotalSeconds
        };

        var stageNames = new[] { "Outliner", "Writer", "Editor", "FactChecker" };
        for (int i = 0; i < pipelineResult.AgentResults.Count; i++)
        {
            var r = pipelineResult.AgentResults[i];
            result.Stages.Add(new AgentStageResultDto
            {
                StageName = i < stageNames.Length ? stageNames[i] : $"Stage {i+1}",
                Content = r.Content ?? string.Empty,
                IsSuccess = r.IsSuccess,
                ErrorMessage = r.Error?.Message ?? string.Empty
            });
        }

        return result;
    }
}
