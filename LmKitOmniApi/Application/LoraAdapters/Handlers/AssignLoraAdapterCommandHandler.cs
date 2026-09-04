using LmKitOmniApi.Application.LoraAdapters.Commands;
using LmKitOmniApi.Infrastructure.AI.Lora;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.LoraAdapters.Handlers;

/// <summary>
/// Binds/unbinds a LoRA adapter to a custom agent. Owner-scoped: the agent must be owned
/// by the caller inside the tenant (a missing/foreign agent → AgentNotFound, never 403).
/// A non-null adapter id must be an existing tenant registration; null clears the binding.
/// </summary>
public sealed class AssignLoraAdapterCommandHandler
    : IRequestHandler<AssignLoraAdapterCommand, LoraAssignResult>
{
    private readonly HermesDbContext _db;
    private readonly ILoraAdapterService _service;

    public AssignLoraAdapterCommandHandler(HermesDbContext db, ILoraAdapterService service)
    {
        _db = db;
        _service = service;
    }

    public async Task<LoraAssignResult> Handle(AssignLoraAdapterCommand request, CancellationToken cancellationToken)
    {
        if (!_service.Enabled)
            return LoraAssignResult.FeatureDisabled();

        var agent = await _db.CustomAgents
            .FirstOrDefaultAsync(a => a.Id == request.AgentId
                && a.TenantId == request.TenantId
                && a.OwnerUserId == request.UserId,
                cancellationToken);
        if (agent is null)
            return LoraAssignResult.AgentNotFound();

        if (request.LoraAdapterId is Guid adapterId)
        {
            var adapterExists = await _db.LoraAdapterRegistrations
                .AnyAsync(a => a.Id == adapterId && a.TenantId == request.TenantId, cancellationToken);
            if (!adapterExists)
                return LoraAssignResult.AdapterNotFound();
        }

        agent.LoraAdapterId = request.LoraAdapterId;
        agent.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return LoraAssignResult.Success();
    }
}
