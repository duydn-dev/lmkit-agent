using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Documents.Queries;
using LmKitOmniApi.Infrastructure.Data;

namespace LmKitOmniApi.Application.Documents.Handlers;

public class ListDocumentsQueryHandler : IRequestHandler<ListDocumentsQuery, List<DocumentListItemDto>>
{
    private readonly HermesDbContext _dbContext;

    public ListDocumentsQueryHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<DocumentListItemDto>> Handle(ListDocumentsQuery request, CancellationToken cancellationToken)
    {
        return await _dbContext.Documents
            .Include(d => d.User)
            .Where(d => d.User != null
                && d.User.TenantId == request.TenantId
                && ((request.IsAdmin && !request.OwnedOnly) || d.UserId == request.UserId))
            .OrderByDescending(d => d.UploadedAt)
            .Select(d => new DocumentListItemDto
            {
                Id = d.Id,
                FileName = d.FileName,
                UploadedAt = d.UploadedAt,
                IsVectorized = d.IsVectorized,
                VectorizationStatus = d.VectorizationStatus,
                ProcessingAttempts = d.ProcessingAttempts,
                HasError = d.LastProcessingError != null
            })
            .ToListAsync(cancellationToken);
    }
}
