using MediatR;
using LMKit.TextGeneration;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Application.Vision.Commands;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.Vision.Handlers;

public class AnalyzeImageCommandHandler : IRequestHandler<AnalyzeImageCommand, string>
{
    private static readonly TimeSpan DefaultInferenceTimeout = TimeSpan.FromMinutes(3);
    private readonly LmModelManager _modelManager;
    private readonly ILogger<AnalyzeImageCommandHandler> _logger;

    public AnalyzeImageCommandHandler(
        LmModelManager modelManager,
        ILogger<AnalyzeImageCommandHandler> logger)
    {
        _modelManager = modelManager;
        _logger = logger;
    }

    public async Task<string> Handle(AnalyzeImageCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.ImagePath) || !System.IO.File.Exists(request.ImagePath))
            throw new FileNotFoundException("Image file not found.", request.ImagePath);

        var visionModel = await _modelManager.GetVisionModelAsync(ct: cancellationToken);
        var inferenceLease = await _modelManager.AcquireVisionInferenceAsync(cancellationToken);
        var leaseTransferred = false;

        var chat = new MultiTurnConversation(visionModel);
        var attachment = new LMKit.Data.Attachment(request.ImagePath);
        var message = new ChatHistory.Message(request.Prompt, attachment);

        // LM-Kit's native VLM call is synchronous and may not observe cancellation
        // while a model is stuck in inference. Run it off the request thread and add
        // a hard upper bound so multipart chat cannot remain open forever. The shared
        // vision lease is intentionally held until the native call has returned.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultInferenceTimeout);
        try
        {
            var inferenceTask = Task.Run(
                () => chat.Submit(message, timeout.Token),
                CancellationToken.None);
            var completed = await Task.WhenAny(
                inferenceTask,
                Task.Delay(DefaultInferenceTimeout, CancellationToken.None));

            if (completed != inferenceTask)
            {
                timeout.Cancel();
                leaseTransferred = true;
                _ = ReleaseVisionLeaseWhenFinishedAsync(inferenceTask, inferenceLease, request.ImagePath);
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                _logger.LogWarning("Vision inference timed out after {Timeout} for {ImagePath}", DefaultInferenceTimeout, request.ImagePath);
                throw new TimeoutException("Image analysis timed out. Please try a smaller or clearer image.");
            }

            var result = await inferenceTask;
            return result.Completion;
        }
        finally
        {
            if (!leaseTransferred)
                await inferenceLease.DisposeAsync();
        }
    }

    private async Task ReleaseVisionLeaseWhenFinishedAsync(
        Task inferenceTask,
        IAsyncDisposable inferenceLease,
        string imagePath)
    {
        try
        {
            await inferenceTask;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Timed-out vision inference ended after the request was released for {ImagePath}", imagePath);
        }
        finally
        {
            await inferenceLease.DisposeAsync();
        }
    }
}
