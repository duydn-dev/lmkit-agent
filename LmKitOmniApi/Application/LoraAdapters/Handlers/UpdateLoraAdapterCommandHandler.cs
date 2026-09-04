using LmKitOmniApi.Application.LoraAdapters.Commands;
using LmKitOmniApi.Infrastructure.AI.Lora;
using MediatR;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Application.LoraAdapters.Handlers;

public sealed class UpdateLoraAdapterCommandHandler
    : IRequestHandler<UpdateLoraAdapterCommand, LoraAdapterMutationResult>
{
    private readonly ILoraAdapterService _service;
    private readonly LoraOptions _options;

    public UpdateLoraAdapterCommandHandler(ILoraAdapterService service, IOptions<LoraOptions> options)
    {
        _service = service;
        _options = options.Value;
    }

    public async Task<LoraAdapterMutationResult> Handle(UpdateLoraAdapterCommand request, CancellationToken cancellationToken)
    {
        if (!_service.Enabled)
            return LoraAdapterMutationResult.FeatureDisabled();

        var validationError = LoraAdapterRules.Validate(request.Name, request.Scale, _options.MinScale, _options.MaxScale);
        if (validationError is not null)
            return LoraAdapterMutationResult.ValidationFailed(validationError);

        try
        {
            var registration = await _service.UpdateAsync(
                request.TenantId, request.Id, request.Name, request.Scale, request.IsActive, cancellationToken);
            return registration is null
                ? LoraAdapterMutationResult.NotFound()
                : LoraAdapterMutationResult.Success(LoraAdapterRules.ToDto(registration));
        }
        catch (LoraAdapterValidationException ex)
        {
            return LoraAdapterMutationResult.ValidationFailed(ex.Message);
        }
    }
}
