using LMKit.TextGeneration;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.Speech.Commands;
using LmKitOmniApi.Application.TextAnalysis.Commands;
using LmKitOmniApi.Application.Vision.Commands;
using LmKitOmniApi.Infrastructure.AI.Agents;
using LmKitOmniApi.Infrastructure.AI.Mcp;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.AI.Web;
using LmKitOmniApi.Services;
using MediatR;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace LmKitOmniApi.Infrastructure.AI.Tools;

/// <summary>
/// Executes the concrete tool-action cases (RAG, VISION, SPEECH, NLP, WEB_SEARCH,
/// DELEGATE, MCP proxy, SUMMARIZE, CODE, PYTHON) that previously lived inline in
/// <see cref="AgentOrchestrator"/>'s ExecuteActionCoreAsync switch.
/// Mechanically extracted with zero behavior change: same guards, same
/// ValidateOwnedPath checks, same error strings, same permission recording and
/// same cancellation semantics. RBAC, sandbox, resilience and audit wrapping all
/// remain in the orchestrator, which invokes this dispatcher inside those layers.
/// Constructed directly by <see cref="AgentOrchestrator"/> with its own injected
/// dependencies — intentionally NOT registered in DI, so the extraction stays
/// self-contained.
/// </summary>
public sealed class AgentActionDispatcher
{
    /// <summary>Number of knowledge-base chunks retrieved per RAG query.</summary>
    private const int KnowledgeBaseTopK = 3;

    /// <summary>Maximum result count requested from the web-search provider.</summary>
    private const int WebSearchMaxResults = 5;

    /// <summary>Character cap on content injected into the summarization prompt.</summary>
    private const int SummarizeContextMaxChars = 3000;

    // H6 Fix: Regex patterns for robust file path extraction
    private static readonly Regex ImagePathRegex = new(
        @"(?:^|\s)(?:""([^""]+\.(jpg|jpeg|png|bmp|webp))""|([^""\s]+\.(jpg|jpeg|png|bmp|webp)))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AudioPathRegex = new(
        @"(?:^|\s)(?:""([^""]+\.(wav|mp3|flac))""|([^""\s]+\.(wav|mp3|flac)))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IRagPipelineService _ragService;
    private readonly IMediator _mediator;
    private readonly IWebSearchService _webSearch;
    private readonly IToolPermissionService _toolPermission;
    private readonly IExecutionSandboxEngine _executionSandbox;
    private readonly IPythonCodeExecutor _pythonExecutor;
    private readonly IBrowserFetchExecutor _browserExecutor;
    private readonly IWebReadService _webRead;
    private readonly LmKitOmniApi.Infrastructure.AI.Database.DbQueryService _dbQuery;
    private readonly UserResourceAccessService _resources;
    private readonly MultiAgentOrchestrator _multiAgent;
    private readonly McpClientService _mcpClient;
    private readonly LmModelManager _modelManager;
    private readonly PromptTemplateEngine _promptTemplate;
    private readonly LmKitOmniApi.Infrastructure.AI.Documents.IPdfFormService _pdfForm;
    private readonly LmKitOmniApi.Infrastructure.AI.Documents.IDocumentRedactionService _documentRedaction;
    private readonly ILogger _logger;

    public AgentActionDispatcher(
        IRagPipelineService ragService,
        IMediator mediator,
        IWebSearchService webSearch,
        IToolPermissionService toolPermission,
        IExecutionSandboxEngine executionSandbox,
        IPythonCodeExecutor pythonExecutor,
        IBrowserFetchExecutor browserExecutor,
        IWebReadService webRead,
        LmKitOmniApi.Infrastructure.AI.Database.DbQueryService dbQuery,
        UserResourceAccessService resources,
        MultiAgentOrchestrator multiAgent,
        McpClientService mcpClient,
        LmModelManager modelManager,
        PromptTemplateEngine promptTemplate,
        LmKitOmniApi.Infrastructure.AI.Documents.IPdfFormService pdfForm,
        LmKitOmniApi.Infrastructure.AI.Documents.IDocumentRedactionService documentRedaction,
        ILogger logger)
    {
        _ragService = ragService;
        _mediator = mediator;
        _webSearch = webSearch;
        _toolPermission = toolPermission;
        _executionSandbox = executionSandbox;
        _pythonExecutor = pythonExecutor;
        _browserExecutor = browserExecutor;
        _webRead = webRead;
        _dbQuery = dbQuery;
        _resources = resources;
        _multiAgent = multiAgent;
        _mcpClient = mcpClient;
        _modelManager = modelManager;
        _promptTemplate = promptTemplate;
        _pdfForm = pdfForm;
        _documentRedaction = documentRedaction;
        _logger = logger;
    }

    /// <summary>
    /// Dispatches one action to its handler. Signature mirrors the orchestrator's
    /// ExecuteActionCoreAsync; the caller is responsible for permission checks,
    /// sandboxing, resilience and audit.
    /// <paramref name="options"/> carries the per-request switches
    /// (AgentRequestOptions: web-search toggle, custom-agent tool whitelist and
    /// knowledge scope), threaded through as a call argument — this dispatcher is
    /// constructed once per orchestrator and must hold no per-request state.
    /// AllowWebSearch=false makes WEB_SEARCH refuse instead of executing, and a
    /// non-null AllowedTools whitelist refuses any action whose mapped permission
    /// name is not whitelisted: defense-in-depth behind the planner-level tool
    /// exclusion, and a pure narrowing of (never a substitute for) the RBAC check
    /// the orchestrator already ran.
    /// </summary>
    public async Task<string> ExecuteAsync(
        Guid tenantId, Guid? userId, string userRole, string query, string action, AgentRequestOptions? options,
        CancellationToken ct, IList<LmKitOmniApi.Infrastructure.AI.Security.ProducedFile>? fileSink = null)
    {
        if (options?.AllowedTools is { } whitelist && !IsActionWhitelisted(action, whitelist))
            return "[Công cụ này không khả dụng cho agent hiện tại]";

        switch (action)
        {
            case "RAG":
                return await ExecuteRagQueryAsync(tenantId, userId, query, options?.KnowledgeDocumentIds, ct);

            case "VISION":
                return await ExecuteVisionAnalysisAsync(tenantId, userId, query, ct);

            case "SPEECH":
                return await ExecuteSpeechTranscriptionAsync(tenantId, userId, query, ct);

            case "NLP":
                return await ExecuteTextAnalysisAsync(tenantId, userId, query, ct);

            case "WEB_SEARCH":
                if (!(options?.AllowWebSearch ?? true))
                    return "[Tìm kiếm web đang tắt cho phiên này]";
                return await ExecuteWebSearchAsync(tenantId, userId, query, ct);

            // ── Multi-Agent Delegation ──
            case "DELEGATE":
                return await ExecuteDelegationAsync(tenantId, userId, userRole, query, ct);

            // ── MCP External Tool (H5 Fix: query-based tool selection) ──
            case var mcpAction when mcpAction.StartsWith("MCP:", StringComparison.OrdinalIgnoreCase):
                return await ExecuteMcpProxyAsync(tenantId, userId, mcpAction, query, ct);

            // ── Document Summarization ──
            case "SUMMARIZE":
                return await ExecuteSummarizationAsync(query, ct);

            // ── Code Interpreter (v1: sandboxed JavaScript via Jint) ──
            case "CODE":
                return await ExecuteJavaScriptAsync(tenantId, userId, query, ct);

            // ── Code Interpreter (v2: sandboxed Python via isolated container) ──
            case "PYTHON":
                return await ExecutePythonAsync(tenantId, userId, query, fileSink, ct);

            // ── Headless-browser page fetch (read-only "computer-use" slice) ──
            case "BROWSE":
                return await ExecuteBrowseAsync(tenantId, userId, query, ct);

            // ── Native LM-Kit web fetch-and-read (WebReadTool) ──
            case "WEB_FETCH":
                return await ExecuteFetchWebAsync(tenantId, userId, query, ct);

            // ── External database agent (read-only) ──
            case "DBSCHEMA":
                return await ExecuteDbSchemaAsync(tenantId, userId, query, ct);
            case "DBQUERY":
                return await ExecuteDbQueryAsync(tenantId, userId, query, ct);
            case "DBWRITE":
                return await ExecuteDbWriteAsync(tenantId, userId, query, ct);

            // ── Native document tools (PDF forms + PDF/Office redaction + PDF/A) ──
            case "READ_PDF_FORM":
                return await ExecuteReadPdfFormAsync(tenantId, userId, query, ct);
            case "FILL_PDF_FORM":
                return await ExecuteFillPdfFormAsync(tenantId, userId, query, fileSink, ct);
            case "REDACT_PDF":
                return await ExecuteRedactPdfAsync(tenantId, userId, query, fileSink, ct);
            case "REDACT_OFFICE":
                return await ExecuteRedactOfficeAsync(tenantId, userId, query, fileSink, ct);
            case "VALIDATE_PDFA":
                return await ExecuteValidatePdfAAsync(tenantId, userId, query, ct);

            default:
                return $"Unknown action: {action}";
        }
    }

    /// <summary>
    /// Maps the action to its permission tool name (same table the orchestrator
    /// uses for RBAC — <see cref="AgentOrchestrator.ActionToToolMap"/>) and checks
    /// it against the custom agent's whitelist. Unmapped actions (dynamic
    /// "MCP:server:tool" names) fall back to the raw action name, which a curated
    /// whitelist never contains — so MCP is excluded whenever a whitelist is set.
    /// </summary>
    private static bool IsActionWhitelisted(string action, IReadOnlyCollection<string> whitelist)
    {
        var permissionName = AgentOrchestrator.ActionToToolMap.TryGetValue(action, out var mapped)
            ? mapped
            : action;
        return whitelist.Contains(permissionName, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string> ExecuteRagQueryAsync(
        Guid tenantId, Guid? userId, string query, IReadOnlyCollection<Guid>? documentIds, CancellationToken ct)
    {
        var ragResult = await _ragService.QueryKnowledgeBaseAsync(
            tenantId,
            userId ?? Guid.Empty,
            query,
            topK: KnowledgeBaseTopK,
            ct: ct,
            chatInferenceLeaseAlreadyHeld: true,
            documentIds: documentIds);
        await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "QueryKnowledgeBase", query, ct);
        return ragResult;
    }

    private async Task<string> ExecuteVisionAnalysisAsync(Guid tenantId, Guid? userId, string query, CancellationToken ct)
    {
        // H6 Fix: Use regex for robust file path extraction
        var imageMatch = ImagePathRegex.Match(query);
        var imagePath = imageMatch.Success
            ? (imageMatch.Groups[1].Success ? imageMatch.Groups[1].Value : imageMatch.Groups[3].Value)
            : null;
        if (!string.IsNullOrEmpty(imagePath))
        {
            if (userId is null) return "[File access denied: user identity is required]";
            var pathCheck = _resources.ValidateOwnedPath(tenantId, userId.Value, imagePath);
            if (!pathCheck.IsAllowed) return $"[File access denied: {pathCheck.DenialReason}]";

            var visionResult = await _mediator.Send(new AnalyzeImageCommand { ImagePath = pathCheck.SanitizedPath }, ct);
            await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "AnalyzeImage", imagePath, ct);
            return visionResult;
        }
        return "No image path found in query.";
    }

    private async Task<string> ExecuteSpeechTranscriptionAsync(Guid tenantId, Guid? userId, string query, CancellationToken ct)
    {
        // H6 Fix: Use regex for robust file path extraction
        var audioMatch = AudioPathRegex.Match(query);
        var audioPath = audioMatch.Success
            ? (audioMatch.Groups[1].Success ? audioMatch.Groups[1].Value : audioMatch.Groups[3].Value)
            : null;
        if (!string.IsNullOrEmpty(audioPath))
        {
            if (userId is null) return "[File access denied: user identity is required]";
            var audioPathCheck = _resources.ValidateOwnedPath(tenantId, userId.Value, audioPath);
            if (!audioPathCheck.IsAllowed) return $"[File access denied: {audioPathCheck.DenialReason}]";

            var speechResult = await _mediator.Send(new TranscribeAudioCommand { AudioPath = audioPathCheck.SanitizedPath }, ct);
            await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "TranscribeAudio", audioPath, ct);
            return speechResult.Text;
        }
        return "No audio path found in query.";
    }

    private async Task<string> ExecuteTextAnalysisAsync(Guid tenantId, Guid? userId, string query, CancellationToken ct)
    {
        var nlpResult = await _mediator.Send(new AnalyzeTextCommand
        {
            Text = query,
            ChatInferenceLeaseAlreadyHeld = true
        }, ct);
        await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "AnalyzeText", null, ct);
        return $"Sentiment: {nlpResult.Sentiment}, Entities: {string.Join(", ", nlpResult.ExtractedEntities)}";
    }

    private async Task<string> ExecuteWebSearchAsync(Guid tenantId, Guid? userId, string query, CancellationToken ct)
    {
        var webResult = await _webSearch.SearchWebAsync(query, WebSearchMaxResults, ct);
        await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "SearchWeb", query, ct);
        return webResult;
    }

    private async Task<string> ExecuteDelegationAsync(Guid tenantId, Guid? userId, string userRole, string query, CancellationToken ct)
    {
        _logger.LogInformation("🤖 Delegating to multi-agent system...");
        var agentResult = await _multiAgent.RouteAndExecuteAsync(tenantId, userId, userRole, query, ct);
        await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "Delegate", query, ct);
        return agentResult;
    }

    private async Task<string> ExecuteMcpProxyAsync(Guid tenantId, Guid? userId, string mcpAction, string query, CancellationToken ct)
    {
        var target = mcpAction[4..];
        if (target.StartsWith("TRUSTED_READ:", StringComparison.OrdinalIgnoreCase))
            target = target[13..];
        var separator = target.IndexOf(':');
        if (separator <= 0 || separator == target.Length - 1)
            return "[MCP error: invalid server-qualified tool name]";
        var exactServerName = target[..separator];
        var exactToolName = target[(separator + 1)..];
        Dictionary<string, object>? parameters;
        try
        {
            parameters = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(query);
        }
        catch (System.Text.Json.JsonException)
        {
            return "[MCP error: invalid structured arguments]";
        }

        var exactMcpResult = await _mcpClient.InvokeToolAsync(
            tenantId,
            exactServerName,
            exactToolName,
            parameters ?? new Dictionary<string, object>(),
            ct);
        if (!exactMcpResult.Success)
            throw new InvalidOperationException(exactMcpResult.ErrorMessage ?? "MCP tool failed.");

        await _toolPermission.RecordToolInvocationAsync(
            tenantId, userId, mcpAction, query, ct);
        return exactMcpResult.Content;
    }

    /// <summary>
    /// CODE action: runs the query as JavaScript inside the hard-capped Jint
    /// sandbox (no CLR, no network, no filesystem — see
    /// <see cref="IExecutionSandboxEngine"/>). The whitelist guard at the top of
    /// <see cref="ExecuteAsync"/> covers this case via the CODE → RunCode
    /// mapping, and the orchestrator has already run the RBAC check on
    /// "RunCode". Parameters are recorded as null (like NLP) because the code
    /// snippet may embed user data; the orchestrator's audit layer still stores
    /// the query.
    /// </summary>
    private async Task<string> ExecuteJavaScriptAsync(Guid tenantId, Guid? userId, string query, CancellationToken ct)
    {
        _logger.LogInformation("🧮 Executing JavaScript in the Jint sandbox...");
        var codeResult = await _executionSandbox.ExecuteCodeSafelyAsync(query, "javascript", ct);
        await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "RunCode", null, ct);
        return codeResult;
    }

    /// <summary>
    /// PYTHON action: runs the query as Python 3 inside the OS-isolated container
    /// sandbox (no network, non-root, dropped capabilities, CPU/memory/time
    /// limits — see <see cref="IPythonCodeExecutor"/>). Mirrors the CODE case: the
    /// whitelist guard at the top of <see cref="ExecuteAsync"/> covers this via the
    /// PYTHON → RunPython mapping, and the orchestrator has already run the RBAC
    /// check on "RunPython" and only offered the tool when the interpreter is
    /// enabled. Parameters are recorded as null (like CODE/NLP) because the snippet
    /// may embed user data; the orchestrator's audit layer still stores the query.
    /// The executor never throws for script-level failures — errors come back as
    /// bracketed, agent-readable text.
    /// </summary>
    private async Task<string> ExecutePythonAsync(
        Guid tenantId, Guid? userId, string query,
        IList<LmKitOmniApi.Infrastructure.AI.Security.ProducedFile>? fileSink, CancellationToken ct)
    {
        _logger.LogInformation("🐍 Executing Python in the container sandbox...");
        var codeResult = await _pythonExecutor.ExecuteAsync(query, tenantId, userId ?? Guid.Empty, ct);
        await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "RunPython", null, ct);
        // Produced files (if any) ride a side channel so they bypass the string
        // observation (and its sandbox output cap) on their way to the SSE stream.
        if (fileSink is not null && codeResult.Files.Count > 0)
        {
            foreach (var file in codeResult.Files)
                fileSink.Add(file);
        }
        return codeResult.Output;
    }

    /// <summary>
    /// BROWSE action: fetches and renders ONE web page in the OS-isolated browser
    /// container (see <see cref="IBrowserFetchExecutor"/>) and returns the rendered
    /// text. The single-string query is the URL, optionally "url|instruction" — only
    /// the URL portion is used to navigate (the trailing instruction is context for the
    /// agent's own reasoning, never executed). Mirrors the PYTHON case: the whitelist
    /// guard at the top of <see cref="ExecuteAsync"/> covers this via the BROWSE →
    /// BrowseWeb mapping, and the orchestrator has already run the RBAC check on
    /// "BrowseWeb" (approval-required) and only offered the tool when the browser is
    /// enabled. The executor SSRF-validates the URL and never throws for fetch-level
    /// failures — errors come back as bracketed, agent-readable text. Parameters are
    /// recorded as null (like CODE/PYTHON); the orchestrator's audit layer stores the query.
    /// </summary>
    private async Task<string> ExecuteBrowseAsync(Guid tenantId, Guid? userId, string query, CancellationToken ct)
    {
        _logger.LogInformation("🌐 Fetching a web page in the browser sandbox...");
        var separator = query.IndexOf('|');
        var url = (separator >= 0 ? query[..separator] : query).Trim();
        var fetchResult = await _browserExecutor.FetchAsync(url, tenantId, userId ?? Guid.Empty, ct);
        await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "BrowseWeb", null, ct);
        return fetchResult.Text;
    }

    /// <summary>
    /// WEB_FETCH action: fetches and READS one public web page as clean, length-capped
    /// text with a source citation, via LM-Kit's built-in WebReadTool (see
    /// <see cref="IWebReadService"/>). The single-string query is the URL, optionally
    /// "url|what-to-extract" — the service parses it and fetches only the URL (the
    /// trailing instruction is context for the agent, never executed). Mirrors the
    /// BROWSE case: the whitelist guard at the top of <see cref="ExecuteAsync"/> covers
    /// this via the WEB_FETCH → FetchWeb mapping, the orchestrator has already run the
    /// RBAC check on "FetchWeb" and only offered the tool when the service is enabled.
    /// The service SSRF-validates the URL before any fetch and never throws for
    /// fetch-level failures — errors come back as bracketed, agent-readable text.
    /// Parameters are recorded as null (like BROWSE); the orchestrator's audit layer
    /// stores the query.
    /// </summary>
    private async Task<string> ExecuteFetchWebAsync(Guid tenantId, Guid? userId, string query, CancellationToken ct)
    {
        _logger.LogInformation("🌐 Fetching and reading a web page (LM-Kit WebReadTool)...");
        var result = await _webRead.ReadAsync(query, ct);
        await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "FetchWeb", null, ct);
        return result;
    }

    // ── Native document tools (PDF forms + PDF/Office redaction + PDF/A validation) ──
    // Each takes a small JSON payload: {"path":"<owned file>", …}. The path is
    // re-validated against the caller's isolated store before any bytes are read (defence
    // in depth — the model never supplies an unchecked filesystem path). The services
    // throw DocumentToolsDisabledException / DocumentValidationException, surfaced here as
    // bracketed, agent-readable text (mirrors BROWSE/WEB_FETCH — never throws to the loop).
    // Fill and redact write the derived file into the caller's upload root and add it to
    // fileSink so it streams back as a [FILE:] download (like the PYTHON case).

    private async Task<string> ExecuteReadPdfFormAsync(Guid tenantId, Guid? userId, string query, CancellationToken ct)
    {
        var input = ResolveOwnedDocumentInput(tenantId, userId, query, out var error);
        if (input is null) return error!;
        try
        {
            var snapshot = _pdfForm.GetFields(input.Value.Bytes, ct);
            await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "ReadPdfForm", null, ct);
            return System.Text.Json.JsonSerializer.Serialize(snapshot);
        }
        catch (LmKitOmniApi.Infrastructure.AI.Documents.DocumentToolsDisabledException) { return "[Công cụ tài liệu đang tắt]"; }
        catch (LmKitOmniApi.Infrastructure.AI.Documents.DocumentValidationException ex) { return $"[Tệp không hợp lệ: {ex.Message}]"; }
    }

    private async Task<string> ExecuteFillPdfFormAsync(
        Guid tenantId, Guid? userId, string query,
        IList<LmKitOmniApi.Infrastructure.AI.Security.ProducedFile>? fileSink, CancellationToken ct)
    {
        var input = ResolveOwnedDocumentInput(tenantId, userId, query, out var error);
        if (input is null) return error!;
        var (values, flatten) = ParseFillArgs(input.Value.Payload);
        if (values.Count == 0) return "[Không có giá trị nào để điền — cần \"values\":[{\"name\":…,\"value\":…}]]";
        try
        {
            var (data, report) = _pdfForm.Fill(input.Value.Bytes, values, flatten, ct);
            await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "FillPdfForm", null, ct);
            var fileId = PersistProducedFile(tenantId, userId, data, "filled.pdf", "application/pdf", fileSink);
            return System.Text.Json.JsonSerializer.Serialize(new { fileId, report });
        }
        catch (LmKitOmniApi.Infrastructure.AI.Documents.DocumentToolsDisabledException) { return "[Công cụ tài liệu đang tắt]"; }
        catch (LmKitOmniApi.Infrastructure.AI.Documents.DocumentValidationException ex) { return $"[Tệp không hợp lệ: {ex.Message}]"; }
    }

    private async Task<string> ExecuteRedactPdfAsync(
        Guid tenantId, Guid? userId, string query,
        IList<LmKitOmniApi.Infrastructure.AI.Security.ProducedFile>? fileSink, CancellationToken ct)
    {
        var input = ResolveOwnedDocumentInput(tenantId, userId, query, out var error);
        if (input is null) return error!;
        var (terms, caseSensitive, wholeWord) = ParseRedactArgs(input.Value.Payload);
        if (terms.Count == 0) return "[Không có cụm từ nào để redact — cần \"terms\":[\"…\"]]";
        try
        {
            var (data, report) = _documentRedaction.RedactPdf(input.Value.Bytes, terms, caseSensitive, wholeWord, ct);
            await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "RedactPdf", null, ct);
            var fileId = PersistProducedFile(tenantId, userId, data, "redacted.pdf", "application/pdf", fileSink);
            return System.Text.Json.JsonSerializer.Serialize(new { fileId, report });
        }
        catch (LmKitOmniApi.Infrastructure.AI.Documents.DocumentToolsDisabledException) { return "[Công cụ tài liệu đang tắt]"; }
        catch (LmKitOmniApi.Infrastructure.AI.Documents.DocumentValidationException ex) { return $"[Tệp không hợp lệ: {ex.Message}]"; }
    }

    private async Task<string> ExecuteRedactOfficeAsync(
        Guid tenantId, Guid? userId, string query,
        IList<LmKitOmniApi.Infrastructure.AI.Security.ProducedFile>? fileSink, CancellationToken ct)
    {
        var input = ResolveOwnedDocumentInput(tenantId, userId, query, out var error);
        if (input is null) return error!;
        var (terms, caseSensitive, wholeWord) = ParseRedactArgs(input.Value.Payload);
        if (terms.Count == 0) return "[Không có cụm từ nào để redact — cần \"terms\":[\"…\"]]";
        var ext = System.IO.Path.GetExtension(input.Value.Path);
        if (string.IsNullOrEmpty(ext)) return "[Không xác định được định dạng Office từ đuôi tệp]";
        try
        {
            var (data, report) = _documentRedaction.RedactOffice(input.Value.Bytes, ext, terms, caseSensitive, wholeWord, ct);
            await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "RedactOffice", null, ct);
            var contentType = ext.ToLowerInvariant() switch
            {
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                _ => "application/octet-stream"
            };
            var fileId = PersistProducedFile(tenantId, userId, data, "redacted" + ext, contentType, fileSink);
            return System.Text.Json.JsonSerializer.Serialize(new { fileId, report });
        }
        catch (LmKitOmniApi.Infrastructure.AI.Documents.DocumentToolsDisabledException) { return "[Công cụ tài liệu đang tắt]"; }
        catch (LmKitOmniApi.Infrastructure.AI.Documents.DocumentValidationException ex) { return $"[Tệp không hợp lệ: {ex.Message}]"; }
    }

    private async Task<string> ExecuteValidatePdfAAsync(Guid tenantId, Guid? userId, string query, CancellationToken ct)
    {
        var input = ResolveOwnedDocumentInput(tenantId, userId, query, out var error);
        if (input is null) return error!;
        LMKit.Document.Pdf.PdfAConformanceLevel? level = null;
        if (input.Value.Payload.TryGetProperty("level", out var levelEl)
            && levelEl.ValueKind == System.Text.Json.JsonValueKind.String
            && Enum.TryParse<LMKit.Document.Pdf.PdfAConformanceLevel>(levelEl.GetString(), ignoreCase: true, out var parsed))
        {
            level = parsed;
        }
        try
        {
            var report = _documentRedaction.ValidatePdfA(input.Value.Bytes, level, ct);
            await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "ValidatePdfA", null, ct);
            return System.Text.Json.JsonSerializer.Serialize(report);
        }
        catch (LmKitOmniApi.Infrastructure.AI.Documents.DocumentToolsDisabledException) { return "[Công cụ tài liệu đang tắt]"; }
        catch (LmKitOmniApi.Infrastructure.AI.Documents.DocumentValidationException ex) { return $"[Tệp không hợp lệ: {ex.Message}]"; }
    }

    /// <summary>Parses the tool payload, resolves and reads the owned document file. Returns
    /// null with a bracketed <paramref name="error"/> when the payload/path/ownership fails.</summary>
    private (byte[] Bytes, System.Text.Json.JsonElement Payload, string Path)? ResolveOwnedDocumentInput(
        Guid tenantId, Guid? userId, string query, out string? error)
    {
        error = null;
        if (userId is null) { error = "[Truy cập tệp bị từ chối: cần định danh người dùng]"; return null; }
        System.Text.Json.JsonElement payload;
        try { payload = System.Text.Json.JsonDocument.Parse(query).RootElement; }
        catch { error = "[Payload không hợp lệ: cần JSON dạng {\"path\":\"…\"}]"; return null; }
        if (payload.ValueKind != System.Text.Json.JsonValueKind.Object
            || !payload.TryGetProperty("path", out var pathEl)
            || pathEl.ValueKind != System.Text.Json.JsonValueKind.String
            || string.IsNullOrWhiteSpace(pathEl.GetString()))
        {
            error = "[Thiếu trường \"path\" (đường dẫn tệp của bạn)]"; return null;
        }
        var path = pathEl.GetString()!;
        var check = _resources.ValidateOwnedPath(tenantId, userId.Value, path);
        if (!check.IsAllowed || !System.IO.File.Exists(check.SanitizedPath))
        {
            error = "[Không tìm thấy tệp hoặc tệp không thuộc về bạn]"; return null;
        }
        byte[] bytes;
        try { bytes = System.IO.File.ReadAllBytes(check.SanitizedPath); }
        catch { error = "[Không đọc được tệp]"; return null; }
        return (bytes, payload, path);
    }

    /// <summary>Writes a derived document into the caller's isolated upload root under a
    /// server-generated name and registers it on the file sink for a [FILE:] download.</summary>
    private string PersistProducedFile(
        Guid tenantId, Guid? userId, byte[] data, string friendlyName, string contentType,
        IList<LmKitOmniApi.Infrastructure.AI.Security.ProducedFile>? fileSink)
    {
        var ext = System.IO.Path.GetExtension(friendlyName);
        var storedName = $"{Guid.NewGuid():N}{ext}";
        var dir = _resources.GetUploadDirectory(tenantId, userId ?? Guid.Empty);
        System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, storedName), data);
        fileSink?.Add(new LmKitOmniApi.Infrastructure.AI.Security.ProducedFile(storedName, friendlyName, contentType, data.LongLength));
        return storedName;
    }

    private static (List<(string Name, string Value)> Values, bool Flatten) ParseFillArgs(System.Text.Json.JsonElement payload)
    {
        var values = new List<(string, string)>();
        if (payload.TryGetProperty("values", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var name = item.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String ? n.GetString() : null;
                string? val = null;
                if (item.TryGetProperty("value", out var v))
                    val = v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : v.ToString();
                if (!string.IsNullOrEmpty(name)) values.Add((name!, val ?? string.Empty));
            }
        }
        var flatten = payload.TryGetProperty("flatten", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.True;
        return (values, flatten);
    }

    private static (List<string> Terms, bool CaseSensitive, bool WholeWord) ParseRedactArgs(System.Text.Json.JsonElement payload)
    {
        var terms = new List<string>();
        if (payload.TryGetProperty("terms", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    terms.Add(item.GetString()!);
        }
        var cs = payload.TryGetProperty("caseSensitive", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.True;
        var ww = payload.TryGetProperty("wholeWord", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.True;
        return (terms, cs, ww);
    }

    private async Task<string> ExecuteDbSchemaAsync(Guid tenantId, Guid? userId, string query, CancellationToken ct)
    {
        _logger.LogInformation("🗄️ Retrieving external database schema context...");
        var result = await _dbQuery.GetSchemaAsync(tenantId, query, ct);
        await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "DbQuery", null, ct);
        return result;
    }

    private async Task<string> ExecuteDbQueryAsync(Guid tenantId, Guid? userId, string query, CancellationToken ct)
    {
        _logger.LogInformation("🗄️ Running read-only external database query...");
        // Parameters recorded as null (like CODE/PYTHON) — a statement may embed user
        // data; the orchestrator's audit layer still records the action + duration.
        var result = await _dbQuery.RunQueryAsync(tenantId, query, ct);
        await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "DbQuery", null, ct);
        return result;
    }

    private async Task<string> ExecuteDbWriteAsync(Guid tenantId, Guid? userId, string query, CancellationToken ct)
    {
        // Reached ONLY on the approved-resume path (DbWrite is approval-required, so
        // the first call returns [HITL_APPROVAL_REQUIRED] before getting here). The
        // service backs up the target table before executing.
        _logger.LogInformation("🗄️ Executing APPROVED external database write...");
        var result = await _dbQuery.RunWriteAsync(tenantId, query, ct);
        await _toolPermission.RecordToolInvocationAsync(tenantId, userId, "DbWrite", null, ct);
        return result;
    }

    private async Task<string> ExecuteSummarizationAsync(string query, CancellationToken ct)
    {
        _logger.LogInformation("📝 Summarizing content...");
        var summaryModel = await _modelManager.GetChatModelAsync(ct: ct);
        var summaryChat = new MultiTurnConversation(summaryModel);
        summaryChat.SystemPrompt = _promptTemplate.Render("summarize", new Dictionary<string, string>
        {
            ["agent_name"] = "Hermes",
            ["context"] = query.Length > SummarizeContextMaxChars ? query.Substring(0, SummarizeContextMaxChars) : query
        });
        var summaryResult = summaryChat.Submit("Hãy tóm tắt nội dung trên.", ct);
        return summaryResult.Completion;
    }
}
