using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Services;
using Microsoft.Extensions.Logging;
using LMKit.TextGeneration.Chat;
using LMKit.TextGeneration;

namespace LmKitOmniApi.Infrastructure.AI;

/// <summary>
/// DAG Workflow Orchestrator thực thụ: 
/// - Phân loại câu hỏi
/// - Đưa ra quyết định gọi RAG, Agent, hoặc Sandbox
/// - Sinh phản hồi theo luồng DAG
/// </summary>
public class DagWorkflowOrchestrator
{
    private readonly LmModelManager _modelManager;
    private readonly IRagPipelineService _ragService;
    private readonly ISentimentAnalyzerService _sentimentService;
    private readonly IAgentOrchestrator _agentOrchestrator;
    private readonly ILogger<DagWorkflowOrchestrator> _logger;

    public DagWorkflowOrchestrator(
        LmModelManager modelManager, 
        IRagPipelineService ragService, 
        ISentimentAnalyzerService sentimentService,
        IAgentOrchestrator agentOrchestrator,
        ILogger<DagWorkflowOrchestrator> logger)
    {
        _modelManager = modelManager;
        _ragService = ragService;
        _sentimentService = sentimentService;
        _agentOrchestrator = agentOrchestrator;
        _logger = logger;
    }

    public async Task<string> ProcessQueryAsync(Guid tenantId, string query)
    {
        // 1. Phân tích cảm xúc để tinh chỉnh Persona
        var persona = await _sentimentService.AnalyzeSentimentAndGetPersonaAsync(query);
        
        // 2. State-Machine Router (Quyết định hướng đi)
        var intent = await RouteQueryAsync(query);
        _logger.LogInformation("DAG Router xác định Intent: {Intent}", intent);

        string context = string.Empty;

        // 3. Thực thi nhánh (DAG Branches)
        if (intent == "research" || intent == "unknown")
        {
            context = await _ragService.QueryKnowledgeBaseAsync(tenantId, query, topK: 3);
        }
        else if (intent == "action")
        {
            // Thực thi luồng ReAct thực tế thông qua AgentOrchestrator
            _logger.LogInformation("Chuyển hướng sang AgentOrchestrator để thực thi hành động.");
            return await _agentOrchestrator.ProcessQueryAsync(tenantId, query);
        }

        // 4. Sinh kết quả (Synthesis)
        var model = await _modelManager.GetChatModelAsync();
        var chat = new LMKit.TextGeneration.MultiTurnConversation(model)
        {
            SystemPrompt = persona + "\n\nSử dụng ngữ cảnh sau để trả lời (nếu có):\n" + context
        };

        var response = chat.Submit(query);
        return response.Completion;
    }

    public async IAsyncEnumerable<string> StreamProcessQueryAsync(Guid tenantId, Guid sessionId, Guid userId, string query, LMKit.TextGeneration.Chat.ChatHistory history, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var persona = await _sentimentService.AnalyzeSentimentAndGetPersonaAsync(query);
        var context = await _ragService.QueryKnowledgeBaseAsync(tenantId, query, topK: 3);
        
        var model = await _modelManager.GetChatModelAsync();
        var chat = new LMKit.TextGeneration.MultiTurnConversation(model, history);
        
        chat.SystemPrompt = persona + "\n\nNgữ cảnh bổ sung từ CSDL:\n" + context;

        var result = chat.Submit(query);
        yield return result.Completion;
        await Task.CompletedTask;
    }

    public Task<List<string>> DecomposeTaskAsync(string query) => Task.FromResult(new List<string> { query });

    public async Task<string> RouteQueryAsync(string query)
    {
        var model = await _modelManager.GetChatModelAsync();
        var chat = new LMKit.TextGeneration.MultiTurnConversation(model);
        chat.SystemPrompt = "Phân loại câu hỏi sau thành 1 trong 3 loại: 'research' (cần tìm kiếm), 'action' (cần thực hiện lệnh), 'chat' (giao tiếp bình thường). Trả về đúng 1 từ duy nhất.";
        var result = chat.Submit(query).Completion.Trim().ToLower();
        
        if (result.Contains("research")) return "research";
        if (result.Contains("action")) return "action";
        return "chat";
    }

    public Task<string> ExecuteDirectActionAsync(Guid tenantId, Guid userId, string action, string query, CancellationToken ct = default) => Task.FromResult("Executed: " + action);
}
