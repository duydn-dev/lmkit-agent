using LmKitOmniApi.Application.LoraAdapters.Commands;
using LmKitOmniApi.Infrastructure.AI.Lora;
using MediatR;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Application.LoraAdapters.Handlers;

public sealed class RegisterLoraAdapterCommandHandler
    : IRequestHandler<RegisterLoraAdapterCommand, LoraAdapterMutationResult>
{
    private readonly ILoraAdapterService _service;
    private readonly LoraOptions _options;

    public RegisterLoraAdapterCommandHandler(ILoraAdapterService service, IOptions<LoraOptions> options)
    {
        _service = service;
        _options = options.Value;
    }

    public async Task<LoraAdapterMutationResult> Handle(RegisterLoraAdapterCommand request, CancellationToken cancellationToken)
    {
        if (!_service.Enabled)
            return LoraAdapterMutationResult.FeatureDisabled();

        var validationError = LoraAdapterRules.Validate(
            request.Name, request.Scale, _options.MinScale, _options.MaxScale, request.Description, request.TargetModelId);
        if (validationError is not null)
            return LoraAdapterMutationResult.ValidationFailed(validationError);

        try
        {
            var registration = await _service.RegisterAsync(
                request.TenantId,
                request.Name,
                request.Description,
                request.Content,
                request.ContentLength,
                request.Scale,
                request.TargetModelId,
                cancellationToken);
            return LoraAdapterMutationResult.Success(LoraAdapterRules.ToDto(registration));
        }
        catch (LoraFeatureDisabledException)
        {
            return LoraAdapterMutationResult.FeatureDisabled();
        }
        catch (LoraAdapterValidationException ex)
        {
            return LoraAdapterMutationResult.ValidationFailed(ex.Message);
        }
    }
}
