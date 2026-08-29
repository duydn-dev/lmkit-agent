using LMKit.TextGeneration;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.Speech.Commands;
using LmKitOmniApi.Application.TextAnalysis.Commands;
using LmKitOmniApi.Application.Vision.Commands;
using LmKitOmniApi.Infrastructure.AI.Agents;
using LmKitOmniApi.Infrastructure.AI.Mcp;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Services;
using MediatR;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace LmKitOmniApi.Infrastructure.AI.Tools;

/// <summary>
/// Executes the concrete tool-action cases (RAG, VISION, SPEECH, NLP, WEB_SEARCH,
/// DELEGATE, MCP proxy, SUMMARIZE) that previously lived inline in
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
    private readonly UserResourceAccessService _resources;
    private readonly MultiAgentOrchestrator _multiAgent;
    private readonly McpClientService _mcpClient;
    private readonly LmModelManager _modelManager;
    private readonly PromptTemplateEngine _promptTemplate;
    private readonly ILogger _logger;

    public AgentActionDispatcher(
        IRagPipelineService ragService,
        IMediator mediator,
        IWebSearchService webSearch,
        IToolPermissionService toolPermission,
        UserResourceAccessService resources,
        MultiAgentOrchestrator multiAgent,
        McpClientService mcpClient,
        LmModelManager modelManager,
        PromptTemplateEngine promptTemplate,
        ILogger logger)
    {
        _ragService = ragService;
        _mediator = mediator;
        _webSearch = webSearch;
        _toolPermission = toolPermission;
        _resources = resources;
        _multiAgent = multiAgent;
        _mcpClient = mcpClient;
        _modelManager = modelManager;
        _promptTemplate = promptTemplate;
        _logger = logger;
    }

    /// <summary>
    /// Dispatches one action to its handler. Signature mirrors the orchestrator's
    /// ExecuteActionCoreAsync; the caller is responsible for permission checks,
    /// sandboxing, resilience and audit.
    /// </summary>
    public async Task<string> ExecuteAsync(
        Guid tenantId, Guid? userId, string userRole, string query, string action, CancellationToken ct)
    {
        switch (action)
        {
            case "RAG":
                return await ExecuteRagQueryAsync(tenantId, userId, query, ct);

            case "VISION":
                return await ExecuteVisionAnalysisAsync(tenantId, userId, query, ct);

            case "SPEECH":
                return await ExecuteSpeechTranscriptionAsync(tenantId, userId, query, ct);

            case "NLP":
                return await ExecuteTextAnalysisAsync(tenantId, userId, query, ct);

            case "WEB_SEARCH":
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

            default:
                return $"Unknown action: {action}";
        }
    }

    private async Task<string> ExecuteRagQueryAsync(Guid tenantId, Guid? userId, string query, CancellationToken ct)
    {
        var ragResult = await _ragService.QueryKnowledgeBaseAsync(
            tenantId,
            userId ?? Guid.Empty,
            query,
            topK: KnowledgeBaseTopK,
            ct: ct,
            chatInferenceLeaseAlreadyHeld: true);
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
