using LmKitOmniApi.Application.Abstractions;
using System.Text.RegularExpressions;

namespace LmKitOmniApi.Infrastructure.AI;

public class TextChunkingService : ITextChunkingService
{
    public List<string> ChunkText(string text, int maxChunkSize = 1200, int overlap = 200)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;

        // Basic sliding window chunking by character length
        int i = 0;
        while (i < text.Length)
        {
            int length = Math.Min(maxChunkSize, text.Length - i);
            string chunk = text.Substring(i, length);
            chunks.Add(chunk);
            
            i += (maxChunkSize - overlap);
        }

        return chunks;
    }
}
