using LmKitOmniApi.Application.CustomAgents.Commands;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.CustomAgents.Handlers;

public class UpdateCustomAgentCommandHandler : IRequestHandler<UpdateCustomAgentCommand, SaveCustomAgentResult>
{
    private readonly HermesDbContext _dbContext;

    public UpdateCustomAgentCommandHandler(HermesDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<SaveCustomAgentResult> Handle(UpdateCustomAgentCommand request, CancellationToken cancellationToken)
    {
        // Owner-only: a shared agent is editable by its owner alone. Missing and
        // not-owned are indistinguishable (404), so agent ids are not enumerable.
        var agent = await _dbContext.CustomAgents.FirstOrDefaultAsync(
            candidate => candidate.Id == request.AgentId
                && candidate.TenantId == request.TenantId
                && candidate.OwnerUserId == request.UserId,
            cancellationToken);
        if (agent is null)
            return SaveCustomAgentResult.NotFound();

        var validationError = await CustomAgentRules.ValidateAsync(_dbContext, request, cancellationToken);
        if (validationError is not null)
            return SaveCustomAgentResult.ValidationFailed(validationError);

        agent.Name = request.Name.Trim();
        agent.Description = NormalizeOptional(request.Description);
        agent.Icon = NormalizeOptional(request.Icon);
        agent.PersonaPrompt = request.PersonaPrompt.Trim();
        agent.AllowedToolsCsv = CustomAgentRules.BuildToolsCsv(request.AllowedTools);
        agent.KnowledgeDocumentIdsCsv = CustomAgentRules.BuildDocumentIdsCsv(request.KnowledgeDocumentIds);
        agent.IsSharedWithTenant = request.IsSharedWithTenant;
        agent.UpdatedAtUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return SaveCustomAgentResult.Success();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
