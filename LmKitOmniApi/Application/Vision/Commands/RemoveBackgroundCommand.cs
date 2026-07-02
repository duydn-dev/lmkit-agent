using MediatR;

namespace LmKitOmniApi.Application.Vision.Commands;

public class RemoveBackgroundCommand : IRequest<RemoveBackgroundResult>
{
    public string ImagePath { get; set; } = string.Empty;
}

public class RemoveBackgroundResult
{
    public string Base64Image { get; set; } = string.Empty;
}
