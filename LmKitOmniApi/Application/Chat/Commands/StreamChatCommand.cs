using MediatR;

namespace LmKitOmniApi.Application.Chat.Commands;

public class StreamChatCommand : IStreamRequest<string>
{
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// Re-run the session's last user message. The incoming <see cref="Message"/> is
    /// ignored, trailing assistant replies are deleted and no new user row is stored.
    /// Mutually exclusive with <see cref="ReplaceLastExchange"/> (enforced in ChatController).
    /// </summary>
    public bool Regenerate { get; set; }

    /// <summary>
    /// Edit-last: deletes the last user message and its trailing assistant replies,
    /// then proceeds like a normal send with the provided <see cref="Message"/>.
    /// Mutually exclusive with <see cref="Regenerate"/> (enforced in ChatController).
    /// </summary>
    public bool ReplaceLastExchange { get; set; }

    /// <summary>Per-request toggle forwarded to the orchestrator as AgentRequestOptions.AllowWebSearch.</summary>
    public bool EnableWebSearch { get; set; } = true;
}
