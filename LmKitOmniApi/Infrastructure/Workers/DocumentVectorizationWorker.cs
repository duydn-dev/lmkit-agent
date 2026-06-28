using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Services;
using LMKit.Document.Conversion;
using LmKitOmniApi.Domain.Entities;

namespace LmKitOmniApi.Infrastructure.Workers;

public class DocumentVectorizationWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DocumentVectorizationWorker> _logger;

    public DocumentVectorizationWorker(IServiceProvider serviceProvider, ILogger<DocumentVectorizationWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background Job: Document Vectorization Worker is starting.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
                
                var unvectorizedDocs = await dbContext.Documents
                    .Include(d => d.User)
                    .Where(d => !d.IsVectorized)
                    .ToListAsync(stoppingToken);

                if (!unvectorizedDocs.Any()) continue;

                var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStoreService>();
                var chunkingService = scope.ServiceProvider.GetRequiredService<ITextChunkingService>();
                var modelManager = scope.ServiceProvider.GetRequiredService<LmModelManager>();

                var embeddingModel = await modelManager.GetEmbeddingModelAsync();
                var embedder = new LMKit.Embeddings.Embedder(embeddingModel);
                var config = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
                string collectionName = config["VectorStore:CollectionName"] ?? "lmkit_chunks";

                await vectorStore.EnsureCollectionExistsAsync(collectionName, (ulong)embeddingModel.EmbeddingSize);
                var converter = new DocumentToMarkdown();

                foreach (var doc in unvectorizedDocs)
                {
                    _logger.LogInformation($"Processing Document: {doc.FileName}");

                    if (!File.Exists(doc.FilePath))
                    {
                        _logger.LogWarning($"File not found on disk: {doc.FilePath}. Marking as vectorized to skip.");
                        doc.IsVectorized = true;
                        continue;
                    }

                    var conversionResult = converter.Convert(doc.FilePath, new DocumentToMarkdownOptions());
                    var textContent = conversionResult.Markdown;
                    
                    var chunks = chunkingService.ChunkText(textContent);

                    foreach (var textChunk in chunks)
                    {
                        var vectorId = Guid.NewGuid(); // ID for Qdrant
                        var vector = embedder.GetEmbeddings(textChunk);

                        var chunkEntity = new DocumentChunk
                        {
                            DocumentId = doc.Id,
                            Content = textChunk,
                            TokenCount = textChunk.Split(' ').Length,
                            VectorId = vectorId
                        };
                        
                        dbContext.DocumentChunks.Add(chunkEntity);

                        // Upload to Qdrant with powerful Hybrid Search Metadata
                        var payload = new Dictionary<string, object>
                        {
                            { "ChunkId", chunkEntity.Id.ToString() },
                            { "DocumentId", doc.Id.ToString() },
                            { "TenantId", doc.User?.TenantId.ToString() ?? "Anonymous" },
                            { "FileName", doc.FileName },
                            { "Content", textChunk }
                        };

                        await vectorStore.UpsertVectorAsync(collectionName, vectorId, vector, payload);
                    }

                    doc.IsVectorized = true;
                    await dbContext.SaveChangesAsync(stoppingToken);
                    _logger.LogInformation($"Successfully vectorized {chunks.Count} chunks for {doc.FileName}.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in Vectorization Job: {ex.Message}");
            }
        }
    }
}
