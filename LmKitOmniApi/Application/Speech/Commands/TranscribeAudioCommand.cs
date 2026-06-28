using MediatR;

namespace LmKitOmniApi.Application.Speech.Commands;

public class TranscribeAudioCommand : IRequest<TranscribeAudioResult>
{
    public string AudioPath { get; set; } = string.Empty;
}

public class TranscribeAudioResult
{
    public string Text { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
}
