using LmKitOmniApi.Application.LoraAdapters.Queries;
using LmKitOmniApi.Infrastructure.AI.Lora;
using MediatR;

namespace LmKitOmniApi.Application.LoraAdapters.Handlers;

public sealed class GetLoraAdapterQueryHandler
    : IRequestHandler<GetLoraAdapterQuery, LoraAdapterDto?>
{
    private readonly ILoraAdapterService _service;

    public GetLoraAdapterQueryHandler(ILoraAdapterService service) => _service = service;

    public async Task<LoraAdapterDto?> Handle(GetLoraAdapterQuery request, CancellationToken cancellationToken)
    {
        var registration = await _service.GetAsync(request.TenantId, request.Id, cancellationToken);
        return registration is null ? null : LoraAdapterRules.ToDto(registration);
    }
}
