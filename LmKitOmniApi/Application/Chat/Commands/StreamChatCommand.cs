using MediatR;

namespace LmKitOmniApi.Application.Chat.Commands;

public class StreamChatCommand : IStreamRequest<string>
{
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
}
