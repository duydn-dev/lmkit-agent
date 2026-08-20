using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Services;
using LMKit.Document.Conversion;
using LmKitOmniApi.Domain.Entities;
using System.Security.Cryptography;

namespace LmKitOmniApi.Infrastructure.Workers;

public class DocumentVectorizationWorker : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);
    private const int MaximumAttempts = 3;
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
                
                var now = DateTime.UtcNow;
                var candidateIds = await dbContext.Documents
                    .Where(document => !document.IsVectorized
                        && document.ProcessingAttempts < MaximumAttempts
                        && (document.ProcessingLeaseUntilUtc == null || document.ProcessingLeaseUntilUtc < now))
                    .OrderBy(document => document.UploadedAt)
                    .Select(document => document.Id)
                    .Take(10)
                    .ToListAsync(stoppingToken);

                if (candidateIds.Count == 0) continue;

                var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStoreService>();
                var chunkingService = scope.ServiceProvider.GetRequiredService<ITextChunkingService>();
                var modelManager = scope.ServiceProvider.GetRequiredService<LmModelManager>();

                var embeddingModel = await modelManager.GetEmbeddingModelAsync();
                var embedder = new LMKit.Embeddings.Embedder(embeddingModel);
                var config = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
                string collectionName = config["VectorStore:CollectionName"] ?? "lmkit_chunks";

                await vectorStore.EnsureCollectionExistsAsync(collectionName, (ulong)embeddingModel.EmbeddingSize);
                var converter = new DocumentToMarkdown();

                foreach (var documentId in candidateIds)
                {
                    var leaseUntil = DateTime.UtcNow.Add(LeaseDuration);
                    var claimed = await dbContext.Documents
                        .Where(document => document.Id == documentId
                            && !document.IsVectorized
                            && document.ProcessingAttempts < MaximumAttempts
                            && (document.ProcessingLeaseUntilUtc == null || document.ProcessingLeaseUntilUtc < DateTime.UtcNow))
                        .ExecuteUpdateAsync(update => update
                            .SetProperty(document => document.VectorizationStatus, Document.ProcessingStatus)
                            .SetProperty(document => document.ProcessingLeaseUntilUtc, leaseUntil)
                            .SetProperty(document => document.ProcessingAttempts, document => document.ProcessingAttempts + 1)
                            .SetProperty(document => document.LastProcessingError, (string?)null), stoppingToken);
                    if (claimed != 1) continue;

                    var doc = await dbContext.Documents
                        .Include(document => document.User)
                        .Include(document => document.Chunks)
                        .SingleAsync(document => document.Id == documentId, stoppingToken);
                    _logger.LogInformation("Processing document {DocumentId} ({FileName})", doc.Id, doc.FileName);

                    if (!File.Exists(doc.FilePath))
                    {
                        _logger.LogWarning("Document file is missing for {DocumentId}", doc.Id);
                        doc.VectorizationStatus = Document.FailedStatus;
                        doc.LastProcessingError = "Source file is missing.";
                        doc.ProcessingLeaseUntilUtc = null;
                        await dbContext.SaveChangesAsync(stoppingToken);
                        continue;
                    }

                    try
                    {
                        var oldVectorIds = doc.Chunks.Select(chunk => chunk.VectorId).Distinct().ToArray();
                        if (oldVectorIds.Length > 0)
                            await vectorStore.DeleteVectorsAsync(collectionName, oldVectorIds, stoppingToken);
                        dbContext.DocumentChunks.RemoveRange(doc.Chunks);

                        var conversionResult = converter.Convert(doc.FilePath, new DocumentToMarkdownOptions());
                        var chunks = chunkingService.ChunkText(conversionResult.Markdown);

                        for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
                        {
                            var textChunk = chunks[chunkIndex];
                            var vectorId = CreateDeterministicVectorId(doc.Id, chunkIndex);
                            var vector = embedder.GetEmbeddings(textChunk);
                            var chunkEntity = new DocumentChunk
                            {
                                DocumentId = doc.Id,
                                Content = textChunk,
                                TokenCount = textChunk.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                                VectorId = vectorId
                            };
                            dbContext.DocumentChunks.Add(chunkEntity);

                            var payload = new Dictionary<string, object>
                            {
                                { "ChunkId", chunkEntity.Id.ToString() },
                                { "DocumentId", doc.Id.ToString() },
                                { "TenantId", doc.User?.TenantId.ToString() ?? "Anonymous" },
                                { "OwnerUserId", doc.UserId.ToString() },
                                { "AccessScope", doc.User is null ? "denied" : $"private:{doc.User.TenantId:N}:{doc.UserId:N}" },
                                { "FileName", doc.FileName },
                                { "ChunkIndex", chunkIndex },
                                { "Content", textChunk }
                            };
                            await vectorStore.UpsertVectorAsync(collectionName, vectorId, vector, payload);
                        }

                        doc.IsVectorized = true;
                        doc.VectorizationStatus = Document.CompletedStatus;
                        doc.ProcessingLeaseUntilUtc = null;
                        doc.LastProcessingError = null;
                        await dbContext.SaveChangesAsync(stoppingToken);
                        _logger.LogInformation("Successfully vectorized {ChunkCount} chunks for document {DocumentId}", chunks.Count, doc.Id);
                    }
                    catch (Exception ex)
                    {
                        doc.VectorizationStatus = Document.FailedStatus;
                        doc.ProcessingLeaseUntilUtc = null;
                        doc.LastProcessingError = "Vectorization failed. See server logs for details.";
                        await dbContext.SaveChangesAsync(stoppingToken);
                        _logger.LogError(ex, "Vectorization failed for document {DocumentId}", doc.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in document vectorization worker");
            }
        }
    }

    private static Guid CreateDeterministicVectorId(Guid documentId, int chunkIndex)
    {
        var input = System.Text.Encoding.UTF8.GetBytes($"{documentId:N}:{chunkIndex}");
        var hash = SHA256.HashData(input);
        return new Guid(hash.AsSpan(0, 16));
    }
}
