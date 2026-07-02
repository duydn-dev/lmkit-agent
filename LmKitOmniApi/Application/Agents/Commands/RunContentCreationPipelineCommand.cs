using MediatR;

namespace LmKitOmniApi.Application.Agents.Commands;

public class RunContentCreationPipelineCommand : IRequest<RunContentCreationPipelineResult>
{
    public string Topic { get; set; } = string.Empty;
}

public class RunContentCreationPipelineResult
{
    public string FinalContent { get; set; } = string.Empty;
    public List<AgentStageResultDto> Stages { get; set; } = new();
    public double TotalDurationSeconds { get; set; }
}

public class AgentStageResultDto
{
    public string StageName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsSuccess { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}
