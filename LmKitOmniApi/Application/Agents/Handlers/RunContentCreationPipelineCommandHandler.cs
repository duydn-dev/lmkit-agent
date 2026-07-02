using MediatR;
using LMKit.Agents;
using LMKit.Agents.Orchestration;
using LmKitOmniApi.Application.Agents.Commands;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.Agents.Handlers;

public class RunContentCreationPipelineCommandHandler : IRequestHandler<RunContentCreationPipelineCommand, RunContentCreationPipelineResult>
{
    private readonly LmModelManager _modelManager;

    public RunContentCreationPipelineCommandHandler(LmModelManager modelManager)
    {
        _modelManager = modelManager;
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
            .WithPersona(@"FactChecker - You are a Fact-Checker. Review content for accuracy, add qualifiers or disclaimers if needed. Output final content.")
            .WithPlanning(PlanningStrategy.None)
            .Build();

        var pipeline = new PipelineOrchestrator()
            .AddStage("Outliner", outlinerAgent)
            .AddStage("Writer", writerAgent)
            .AddStage("Editor", editorAgent)
            .AddStage("FactChecker", factCheckerAgent);

        var pipelineResult = await pipeline.ExecuteAsync(
            $"Create content about: {request.Topic}",
            cancellationToken);

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
