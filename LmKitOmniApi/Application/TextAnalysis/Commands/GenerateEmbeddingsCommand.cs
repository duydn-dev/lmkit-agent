using MediatR;

namespace LmKitOmniApi.Application.TextAnalysis.Commands;

public class GenerateEmbeddingsCommand : IRequest<GenerateEmbeddingsResult>
{
    public string Text { get; set; } = string.Empty;
}

public class GenerateEmbeddingsResult
{
    public float[] Embeddings { get; set; } = Array.Empty<float>();
}
