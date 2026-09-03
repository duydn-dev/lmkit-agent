using MediatR;

namespace LmKitOmniApi.Application.Speech.Commands;

/// <summary>
/// Segmented / partial transcription request. Backs <c>POST /api/speech/transcribe-stream</c>
/// which streams partial transcripts over Server-Sent Events.
/// </summary>
public sealed class TranscribeAudioStreamCommand : IStreamRequest<TranscriptionPartial>
{
    public string AudioPath { get; set; } = string.Empty;
    public bool EnableVad { get; set; } = true;
}

public enum TranscriptionPartialKind
{
    /// <summary>An incremental segment emitted while decoding progresses.</summary>
    Partial,

    /// <summary>The final, complete transcript emitted once decoding finishes.</summary>
    Final
}

/// <summary>One streamed transcription event: an incremental segment or the final transcript.</summary>
public sealed class TranscriptionPartial
{
    public TranscriptionPartialKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
    public float Confidence { get; init; }
    public string? Language { get; init; }
}
