using LMKit.Agents.Tools;
using LMKit.Agents.Tools.BuiltIn;
using LMKit.TextGeneration;

namespace LmKitOmniApi.Infrastructure.AI.Tools;

/// <summary>
/// Curated LM-Kit.NET built-in tools for the general chat runtime.
///
/// Only deterministic, low-risk, side-effect-free tools are enabled by default.
/// File-system, document, network and mutating tools must be exposed through the
/// application's permission/sandbox gateway before they are enabled.
/// </summary>
public sealed class LmKitDefaultToolCatalog
{
    private static readonly IReadOnlyList<LmKitToolDescriptor> ToolDescriptors =
    [
        new("calc_arithmetic", "Numeric", ToolActivation.Default, "Deterministic arithmetic."),
        new("datetime_now", "Utility", ToolActivation.Default, "Current date/time without side effects."),
        new("json_parse", "Data", ToolActivation.Default, "Parse JSON supplied in the prompt."),
        new("csv_parse", "Data", ToolActivation.Default, "Parse CSV supplied in the prompt."),
        new("xml_parse", "Data", ToolActivation.Default, "Parse XML supplied in the prompt."),
        new("stats_analysis", "Numeric", ToolActivation.Default, "Descriptive statistics over supplied values."),

        new("filesystem_read", "IO", ToolActivation.SandboxedReadOnly, "Requires an allowlisted path."),
        new("filesystem_list", "IO", ToolActivation.SandboxedReadOnly, "Requires an allowlisted path."),
        new("filesystem_search", "IO", ToolActivation.SandboxedReadOnly, "Requires an allowlisted path."),
        new("document_text_extract", "Document", ToolActivation.SandboxedReadOnly, "Requires file validation and tenant ownership."),
        new("pdf_metadata", "Document", ToolActivation.SandboxedReadOnly, "Requires file validation and tenant ownership."),
        new("pdf_pages", "Document", ToolActivation.SandboxedReadOnly, "Requires file validation and tenant ownership."),
        new("ocr_recognize", "Document", ToolActivation.SandboxedReadOnly, "Requires file validation and tenant ownership."),
        new("web_search", "Net", ToolActivation.SandboxedReadOnly, "Requires outbound URL policy and rate limiting."),
        new("rss_fetch", "Net", ToolActivation.SandboxedReadOnly, "Requires outbound URL policy and rate limiting."),

        new("filesystem_write", "IO", ToolActivation.ApprovalRequired, "Writes user-visible state."),
        new("pdf_split", "Document", ToolActivation.ApprovalRequired, "Creates output files."),
        new("pdf_merge", "Document", ToolActivation.ApprovalRequired, "Creates output files."),
        new("pdf_to_image", "Document", ToolActivation.ApprovalRequired, "Creates output files."),
        new("image_to_pdf", "Document", ToolActivation.ApprovalRequired, "Creates output files."),
        new("pdf_unlock", "Document", ToolActivation.ApprovalRequired, "Changes document protection."),
        new("image_deskew", "Document", ToolActivation.ApprovalRequired, "Creates or modifies image output."),
        new("image_crop", "Document", ToolActivation.ApprovalRequired, "Creates or modifies image output."),
        new("image_resize", "Document", ToolActivation.ApprovalRequired, "Creates or modifies image output."),
    ];

    public IReadOnlyList<LmKitToolDescriptor> DescribeTools() => ToolDescriptors;

    public IReadOnlyList<ITool> GetSafeDefaultTools() =>
    [
        BuiltInTools.CalcArithmetic,
        BuiltInTools.DateTimeNow,
        BuiltInTools.JsonParse,
        BuiltInTools.CsvParse,
        BuiltInTools.XmlParse,
        BuiltInTools.StatsAnalysis,
    ];

    public void RegisterSafeDefaults(MultiTurnConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        foreach (var tool in GetSafeDefaultTools())
        {
            conversation.Tools.Register(tool);
        }
    }
}

public enum ToolActivation
{
    Default,
    SandboxedReadOnly,
    ApprovalRequired,
    Disabled,
}

public sealed record LmKitToolDescriptor(
    string Name,
    string Category,
    ToolActivation Activation,
    string Rationale);
