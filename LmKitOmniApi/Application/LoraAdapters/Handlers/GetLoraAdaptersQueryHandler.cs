using LmKitOmniApi.Application.LoraAdapters.Queries;
using LmKitOmniApi.Infrastructure.AI.Lora;
using MediatR;

namespace LmKitOmniApi.Application.LoraAdapters.Handlers;

public sealed class GetLoraAdaptersQueryHandler
    : IRequestHandler<GetLoraAdaptersQuery, IReadOnlyList<LoraAdapterDto>>
{
    private readonly ILoraAdapterService _service;

    public GetLoraAdaptersQueryHandler(ILoraAdapterService service) => _service = service;

    public async Task<IReadOnlyList<LoraAdapterDto>> Handle(GetLoraAdaptersQuery request, CancellationToken cancellationToken)
    {
        var registrations = await _service.ListAsync(request.TenantId, cancellationToken);
        return registrations.Select(LoraAdapterRules.ToDto).ToList();
    }
}
