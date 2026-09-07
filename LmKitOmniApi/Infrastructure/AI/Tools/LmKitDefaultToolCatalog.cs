using LMKit.Agents.Tools;
using LMKit.Agents.Tools.BuiltIn;
using LMKit.TextGeneration;

namespace LmKitOmniApi.Infrastructure.AI.Tools;

/// <summary>
/// The LM-Kit.NET built-in tools this product registers on the general chat
/// runtime — deterministic, low-risk and side-effect-free, every one of them.
///
/// <para>
/// <b>The table IS the registration.</b> <see cref="DescribeTools"/> and
/// <see cref="GetSafeDefaultTools"/> read one list, and every row carries the
/// factory for the <see cref="ITool"/> it describes, so the catalog cannot
/// describe a tool the runtime does not register — or vice versa. It previously
/// did exactly that: the table listed 24 tools, 18 of them annotated "needs the
/// permission gateway first", while the runtime registered 6. Those 18 rows were
/// a transcription of LM-Kit's own <c>BuiltInTools</c> surface (see the
/// console_net samples) reading like this product's capability list, and nothing
/// but the tests ever read them.
/// </para>
///
/// <para>
/// <b>Where the risky capabilities actually live.</b> They are not "not yet
/// wired" — they ship, through this application's own gated action path
/// (<c>AgentActionDispatcher</c> + <c>IToolPermissionService</c> + approvals)
/// rather than through LM-Kit's built-ins: web search is <c>WEB_SEARCH</c>, page
/// reads are <c>WEB_FETCH</c>/<c>BROWSE</c>, OCR and image reading are
/// <c>VISION</c>, PDF form/redaction/PDF-A work is <c>READ_PDF_FORM</c>,
/// <c>FILL_PDF_FORM</c>, <c>REDACT_PDF</c>, <c>REDACT_OFFICE</c> and
/// <c>VALIDATE_PDFA</c>, and file writes only ever land in the caller's isolated
/// store as a <c>[FILE:]</c> result. The enforced, user-facing catalog of those
/// names is <c>CustomAgentRules.ToolCatalog</c> (GET /api/agents/custom/tools).
/// Adding an LM-Kit built-in that touches the filesystem, the network or a
/// document would bypass all of that, which is why this list stays deliberately
/// small — and why <c>LmKitDefaultToolCatalogTests</c> fails the build if an
/// IO/Net/Document row ever appears in it.
/// </para>
/// </summary>
public sealed class LmKitDefaultToolCatalog
{
    // Each row's factory returns a NEW tool instance per call (that is what the
    // BuiltInTools properties do), so a conversation never shares a tool object
    // with a concurrent one — the behaviour the hand-written list had.
    private static readonly IReadOnlyList<LmKitToolDescriptor> ToolDescriptors =
    [
        new("calc_arithmetic", "Numeric", "Deterministic arithmetic.",
            () => BuiltInTools.CalcArithmetic),
        new("datetime_now", "Utility", "Current date/time without side effects.",
            () => BuiltInTools.DateTimeNow),
        new("json_parse", "Data", "Parse JSON supplied in the prompt.",
            () => BuiltInTools.JsonParse),
        new("csv_parse", "Data", "Parse CSV supplied in the prompt.",
            () => BuiltInTools.CsvParse),
        new("xml_parse", "Data", "Parse XML supplied in the prompt.",
            () => BuiltInTools.XmlParse),
        new("stats_analysis", "Numeric", "Descriptive statistics over supplied values.",
            () => BuiltInTools.StatsAnalysis),
    ];

    /// <summary>
    /// The registered tools, with the risk category and the rationale that
    /// justifies each one. This is the definition the runtime registers from —
    /// not a description of it.
    /// </summary>
    public IReadOnlyList<LmKitToolDescriptor> DescribeTools() => ToolDescriptors;

    /// <summary>
    /// Fresh <see cref="ITool"/> instances for every row of <see cref="DescribeTools"/>,
    /// in table order.
    /// </summary>
    public IReadOnlyList<ITool> GetSafeDefaultTools() =>
        [.. DescribeTools().Select(descriptor => descriptor.CreateTool())];

    /// <summary>
    /// Registers the safe defaults on an already-built conversation.
    ///
    /// <para>
    /// <b>Prefer passing <see cref="GetSafeDefaultTools"/> to <c>ChatConversationFactory.Create</c>.</b>
    /// Registering after construction advertises the tools to the model on the FIRST turn only:
    /// LM-Kit renders the tool catalog into the prompt inside the same
    /// <c>history.MessageCount == 0</c> branch that renders the system prompt, so on a rebuilt
    /// (non-empty) history the catalog has to be seeded into the history itself — which the
    /// factory can only do if it knows the tools before it builds it.
    /// </para>
    /// <para>
    /// Idempotent: <c>overwrite: true</c> so calling this after the factory has already
    /// registered the same tools replaces them with themselves rather than throwing
    /// <see cref="InvalidOperationException"/> on the duplicate name.
    /// </para>
    /// </summary>
    public void RegisterSafeDefaults(MultiTurnConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        foreach (var tool in GetSafeDefaultTools())
        {
            conversation.Tools.Register(tool, overwrite: true);
        }
    }
}

/// <summary>
/// One registered built-in tool: the name the model is told about, the risk
/// bucket the safety test keys off, why it needs no permission gateway, and the
/// factory that produces the tool itself.
/// </summary>
/// <param name="Name">Must equal the created tool's own name — asserted by the catalog tests.</param>
/// <param name="Category">Risk bucket. IO / Net / Document are rejected by the catalog tests.</param>
/// <param name="Rationale">Why this tool is safe to enable with no approval step.</param>
/// <param name="CreateTool">Produces a new LM-Kit tool instance to register.</param>
public sealed record LmKitToolDescriptor(
    string Name,
    string Category,
    string Rationale,
    Func<ITool> CreateTool);
