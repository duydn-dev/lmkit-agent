using MediatR;

namespace LmKitOmniApi.Application.Documents.Commands;

public class ExtractDocumentDataCommand : IRequest<ExtractDocumentDataResult>
{
    public string DocumentPath { get; set; } = string.Empty;
    public string JsonSchema { get; set; } = string.Empty;
}

public class ExtractDocumentDataResult
{
    public string JsonData { get; set; } = string.Empty;
}
