using LmKitOmniApi.Application.CustomAgents.Commands;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.CustomAgents.Handlers;

public class CreateCustomAgentCommandHandler : IRequestHandler<CreateCustomAgentCommand, SaveCustomAgentResult>
{
    private readonly HermesDbContext _dbContext;
    private readonly ILogger<CreateCustomAgentCommandHandler> _logger;

    public CreateCustomAgentCommandHandler(HermesDbContext dbContext, ILogger<CreateCustomAgentCommandHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<SaveCustomAgentResult> Handle(CreateCustomAgentCommand request, CancellationToken cancellationToken)
    {
        var validationError = await CustomAgentRules.ValidateAsync(_dbContext, request, cancellationToken);
        if (validationError is not null)
            return SaveCustomAgentResult.ValidationFailed(validationError);

        var ownedAgentCount = await _dbContext.CustomAgents.CountAsync(
            agent => agent.TenantId == request.TenantId && agent.OwnerUserId == request.UserId,
            cancellationToken);
        if (ownedAgentCount >= CustomAgentRules.MaxAgentsPerUser)
            return SaveCustomAgentResult.ValidationFailed(
                $"Bạn đã đạt giới hạn tối đa {CustomAgentRules.MaxAgentsPerUser} agent tùy chỉnh.");

        var agent = new CustomAgent
        {
            TenantId = request.TenantId,
            OwnerUserId = request.UserId,
            Name = request.Name.Trim(),
            Description = NormalizeOptional(request.Description),
            Icon = NormalizeOptional(request.Icon),
            PersonaPrompt = request.PersonaPrompt.Trim(),
            AllowedToolsCsv = CustomAgentRules.BuildToolsCsv(request.AllowedTools),
            KnowledgeDocumentIdsCsv = CustomAgentRules.BuildDocumentIdsCsv(request.KnowledgeDocumentIds),
            IsSharedWithTenant = request.IsSharedWithTenant
        };

        _dbContext.CustomAgents.Add(agent);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Custom agent {AgentId} created by user {UserId} (tenant {TenantId})",
            agent.Id, request.UserId, request.TenantId);

        return SaveCustomAgentResult.Success(CustomAgentRules.ToDto(agent, request.UserId));
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
