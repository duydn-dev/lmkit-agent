using LmKitOmniApi.Application.LoraAdapters.Commands;
using LmKitOmniApi.Infrastructure.AI.Lora;
using MediatR;

namespace LmKitOmniApi.Application.LoraAdapters.Handlers;

public sealed class DeleteLoraAdapterCommandHandler
    : IRequestHandler<DeleteLoraAdapterCommand, LoraAdapterMutationResult>
{
    private readonly ILoraAdapterService _service;

    public DeleteLoraAdapterCommandHandler(ILoraAdapterService service) => _service = service;

    public async Task<LoraAdapterMutationResult> Handle(DeleteLoraAdapterCommand request, CancellationToken cancellationToken)
    {
        if (!_service.Enabled)
            return LoraAdapterMutationResult.FeatureDisabled();

        var deleted = await _service.DeleteAsync(request.TenantId, request.Id, cancellationToken);
        return deleted ? LoraAdapterMutationResult.Success() : LoraAdapterMutationResult.NotFound();
    }
}
