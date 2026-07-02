using MediatR;

namespace LmKitOmniApi.Application.Speech.Commands;

public class DetectAudioLanguageCommand : IRequest<DetectAudioLanguageResult>
{
    public string AudioPath { get; set; } = string.Empty;
}

public class DetectAudioLanguageResult
{
    public string Language { get; set; } = string.Empty;
    public float Confidence { get; set; }
}
