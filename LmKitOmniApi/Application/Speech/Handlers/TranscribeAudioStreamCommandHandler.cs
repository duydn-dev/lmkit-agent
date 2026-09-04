using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LMKit.Media.Audio;
using LMKit.Speech;
using LmKitOmniApi.Application.Speech.Commands;
using LmKitOmniApi.Services;
using MediatR;

namespace LmKitOmniApi.Application.Speech.Handlers;

/// <summary>
/// Streams partial transcripts for a complete WAV file.
///
/// HONEST LIMITATION: LM-Kit.NET's <see cref="SpeechToText"/> transcribes a whole
/// <see cref="WaveFile"/> — it has no API to feed a growing/live microphone buffer chunk
/// by chunk. What it DOES provide is engine-native segmentation: the <c>OnNewSegment</c>
/// event fires for each decoded segment as decoding progresses. We surface those segments
/// as incremental SSE "partial" events (behind the shared speech inference lease), then a
/// final complete transcript. This is a genuine partial stream, but the input is still a
/// complete file, so end-to-end latency for a truly LIVE speaker (windowing a mic feed,
/// endpointing, barge-in) is LIVE-ONLY and cannot be measured in CI — it needs live audio
/// hardware and a capable model.
/// </summary>
public sealed class TranscribeAudioStreamCommandHandler
    : IStreamRequestHandler<TranscribeAudioStreamCommand, TranscriptionPartial>
{
    private readonly LmModelManager _modelManager;

    public TranscribeAudioStreamCommandHandler(LmModelManager modelManager)
    {
        _modelManager = modelManager;
    }

    public async IAsyncEnumerable<TranscriptionPartial> Handle(
        TranscribeAudioStreamCommand request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.AudioPath) || !File.Exists(request.AudioPath))
            throw new FileNotFoundException("Audio file not found.", request.AudioPath);

        var speechModel = await _modelManager.GetSpeechModelAsync(ct: cancellationToken);
        await using var inferenceLease = await _modelManager.AcquireSpeechInferenceAsync(cancellationToken);

        var engine = new SpeechToText(speechModel)
        {
            EnableVoiceActivityDetection = request.EnableVad
        };
        using var audio = new WaveFile(request.AudioPath);

        // OnNewSegment fires on the engine's decode thread; marshal segments to the reader
        // through an unbounded channel so this async iterator can yield them in order.
        var channel = Channel.CreateUnbounded<TranscriptionPartial>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        void OnSegment(object? _, SpeechToText.OnNewSegmentEventArgs e)
        {
            var segment = e.Segment;
            channel.Writer.TryWrite(new TranscriptionPartial
            {
                Kind = TranscriptionPartialKind.Partial,
                Text = segment.Text ?? string.Empty,
                StartSeconds = segment.Start.TotalSeconds,
                EndSeconds = segment.End.TotalSeconds,
                Confidence = segment.Confidence,
                Language = segment.Language
            });
        }

        engine.OnNewSegment += OnSegment;

        // Run the whole-file decode in the background; complete the channel when it finishes
        // (or faults) so the reader loop below terminates.
        var transcriptionTask = Task.Run(async () =>
        {
            try
            {
                return await engine.TranscribeAsync(audio, cancellationToken: cancellationToken);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        try
        {
            await foreach (var partial in channel.Reader.ReadAllAsync(cancellationToken))
                yield return partial;
        }
        finally
        {
            engine.OnNewSegment -= OnSegment;
        }

        // Surface any decode failure to the caller and emit the final transcript.
        var result = await transcriptionTask;
        yield return new TranscriptionPartial
        {
            Kind = TranscriptionPartialKind.Final,
            Text = result.Text ?? string.Empty
        };
    }
}
