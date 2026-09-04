namespace LmKitOmniApi.Infrastructure.AI;

/// <summary>
/// Operator toggle for DeepSeek-R1-style reasoning display. When enabled, chat
/// requests run the model with reasoning on and stream its chain-of-thought to the
/// client as <c>[REASONING]:</c> markers (rendered in a collapsible panel, separate
/// from the answer). Off by default: reasoning raises latency and token cost and only
/// produces output on models that support it.
/// </summary>
public sealed class ChatReasoningOptions
{
    public const string SectionName = "ChatReasoning";

    public bool Enabled { get; set; }
}
