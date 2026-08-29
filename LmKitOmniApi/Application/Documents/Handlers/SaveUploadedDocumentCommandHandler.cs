using MediatR;
using LmKitOmniApi.Application.Documents.Commands;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.Data;

namespace LmKitOmniApi.Application.Documents.Handlers;

public class SaveUploadedDocumentCommandHandler : IRequestHandler<SaveUploadedDocumentCommand, Guid>
{
    private readonly HermesDbContext _dbContext;
    private readonly UserResourceAccessService _resources;

    public SaveUploadedDocumentCommandHandler(HermesDbContext dbContext, UserResourceAccessService resources)
    {
        _dbContext = dbContext;
        _resources = resources;
    }

    public async Task<Guid> Handle(SaveUploadedDocumentCommand request, CancellationToken cancellationToken)
    {
        var uploadDir = _resources.GetUploadDirectory(request.TenantId, request.UserId);
        Directory.CreateDirectory(uploadDir);
        var filePath = Path.Combine(uploadDir, $"{Guid.NewGuid():N}{request.Extension}");

        await using (var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await request.Content.CopyToAsync(stream, cancellationToken);
        }

        var doc = new LmKitOmniApi.Domain.Entities.Document
        {
            FileName = request.FileName,
            FilePath = filePath,
            UserId = request.UserId,
            IsVectorized = false
        };

        _dbContext.Documents.Add(doc);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return doc.Id;
    }
}
