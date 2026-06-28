using MediatR;
using LMKit.TextGeneration;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Application.Vision.Commands;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Application.Vision.Handlers;

public class AnalyzeImageCommandHandler : IRequestHandler<AnalyzeImageCommand, string>
{
    private readonly LmModelManager _modelManager;

    public AnalyzeImageCommandHandler(LmModelManager modelManager)
    {
        _modelManager = modelManager;
    }

    public async Task<string> Handle(AnalyzeImageCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.ImagePath) || !System.IO.File.Exists(request.ImagePath))
            throw new FileNotFoundException("Image file not found.", request.ImagePath);

        var visionModel = await _modelManager.GetVisionModelAsync();

        var chat = new MultiTurnConversation(visionModel);
        var attachment = new LMKit.Data.Attachment(request.ImagePath);
        var message = new ChatHistory.Message(request.Prompt, attachment);

        var result = chat.Submit(message);
        
        return result.Completion;
    }
}
