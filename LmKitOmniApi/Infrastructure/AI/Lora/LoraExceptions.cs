namespace LmKitOmniApi.Infrastructure.AI.Lora;

/// <summary>
/// Thrown by <see cref="ILoraAdapterService.RegisterAsync"/> when the LoRA feature is
/// disabled (Lora:Enabled = false). The upload endpoint maps this to HTTP 501.
/// </summary>
public sealed class LoraFeatureDisabledException : Exception
{
    public LoraFeatureDisabledException()
        : base("Tính năng LoRA chưa được bật.") { }
}

/// <summary>
/// Thrown by <see cref="ILoraAdapterService.RegisterAsync"/> when an upload is rejected
/// for a content reason — over the size cap, not a valid adapter, or a duplicate name.
/// The upload endpoint maps this to HTTP 400 with the message.
/// </summary>
public sealed class LoraAdapterValidationException : Exception
{
    public LoraAdapterValidationException(string message) : base(message) { }
}
