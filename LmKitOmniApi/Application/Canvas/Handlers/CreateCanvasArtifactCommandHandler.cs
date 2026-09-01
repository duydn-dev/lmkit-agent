using MediatR;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Application.Canvas.Commands;
using LmKitOmniApi.Application.Canvas.Queries;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;

namespace LmKitOmniApi.Application.Canvas.Handlers;

public class CreateCanvasArtifactCommandHandler : IRequestHandler<CreateCanvasArtifactCommand, CanvasArtifactDetailDto?>
{
    private readonly HermesDbContext _dbContext;

    public CreateCanvasArtifactCommandHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CanvasArtifactDetailDto?> Handle(CreateCanvasArtifactCommand request, CancellationToken cancellationToken)
    {
        // Ownership gate (mirrors the Share slice): attaching to a session that
        // is missing or belongs to someone else fails identically, so the
        // endpoint is not an oracle for foreign session ids.
        if (request.ChatSessionId is { } chatSessionId)
        {
            var ownsSession = await _dbContext.ChatSessions.AnyAsync(
                s => s.Id == chatSessionId
                    && s.TenantId == request.TenantId
                    && s.UserId == request.UserId,
                cancellationToken);
            if (!ownsSession) return null;
        }

        // First version: the root IS the row (RootId == Id, Version == 1).
        var id = Guid.NewGuid();
        var artifact = new CanvasArtifact
        {
            Id = id,
            RootId = id,
            TenantId = request.TenantId,
            UserId = request.UserId,
            ChatSessionId = request.ChatSessionId,
            Title = request.Title,
            Kind = request.Kind,
            Language = request.Language,
            Content = request.Content,
            Version = 1,
            CreatedAtUtc = DateTime.UtcNow
        };
        _dbContext.CanvasArtifacts.Add(artifact);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new CanvasArtifactDetailDto
        {
            Id = artifact.Id,
            RootId = artifact.RootId,
            Title = artifact.Title,
            Kind = artifact.Kind,
            Language = artifact.Language,
            Version = artifact.Version,
            Content = artifact.Content,
            ChatSessionId = artifact.ChatSessionId,
            CreatedAt = artifact.CreatedAtUtc
        };
    }
}
