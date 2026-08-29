using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.Documents.Commands;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.Data;

namespace LmKitOmniApi.Application.Documents.Handlers;

public class DeleteDocumentCommandHandler : IRequestHandler<DeleteDocumentCommand, bool>
{
    private readonly HermesDbContext _dbContext;
    private readonly IVectorStoreService _vectorStore;
    private readonly UserResourceAccessService _resources;
    private readonly string _vectorCollectionName;

    public DeleteDocumentCommandHandler(
        HermesDbContext dbContext,
        IVectorStoreService vectorStore,
        UserResourceAccessService resources,
        IConfiguration configuration)
    {
        _dbContext = dbContext;
        _vectorStore = vectorStore;
        _resources = resources;
        _vectorCollectionName = configuration["VectorStore:CollectionName"] ?? "lmkit_chunks";
    }

    public async Task<bool> Handle(DeleteDocumentCommand request, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents
            .Include(item => item.User)
            .Include(item => item.Chunks)
            .FirstOrDefaultAsync(item => item.Id == request.DocumentId
                && item.User != null
                && item.User.TenantId == request.TenantId
                && (request.IsAdmin || item.UserId == request.UserId), cancellationToken);
        if (document is null) return false;

        var vectorIds = document.Chunks.Select(chunk => chunk.VectorId).Distinct().ToArray();
        if (vectorIds.Length > 0)
            await _vectorStore.DeleteVectorsAsync(_vectorCollectionName, vectorIds, cancellationToken);

        var ownedPath = _resources.ValidateOwnedPath(request.TenantId, document.UserId, document.FilePath);
        if (ownedPath.IsAllowed && File.Exists(ownedPath.SanitizedPath))
            File.Delete(ownedPath.SanitizedPath);

        _dbContext.Documents.Remove(document);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
