using System.Runtime.CompilerServices;
using MediatR;
using Microsoft.EntityFrameworkCore;
using LMKit.TextGeneration;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Application.Chat.Commands;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Services;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Domain.Entities;
using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;

namespace LmKitOmniApi.Application.Chat.Handlers;

public class StreamChatCommandHandler : IStreamRequestHandler<StreamChatCommand, string>
{
    private readonly LmModelManager _modelManager;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly ITokenManagementService _tokenManagement;
    private readonly HermesDbContext _dbContext;
    private readonly IDistributedCache _cache;

    // Maximum messages to load from DB (absolute cap)
    private const int MaxMessagesToLoad = 50;
    // Token budget for conversation history
    private const int HistoryTokenBudget = 3000;

    public StreamChatCommandHandler(
        LmModelManager modelManager, 
        IAgentOrchestrator orchestrator, 
        ITokenManagementService tokenManagement,
        HermesDbContext dbContext,
        IDistributedCache cache)
    {
        _modelManager = modelManager;
        _orchestrator = orchestrator;
        _tokenManagement = tokenManagement;
        _dbContext = dbContext;
        _cache = cache;
    }

    public async IAsyncEnumerable<string> Handle(StreamChatCommand request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var model = await _modelManager.GetChatModelAsync(request.ModelId);
        
        var session = await _dbContext.ChatSessions
            .FirstOrDefaultAsync(s => s.Id == request.SessionId && s.TenantId == request.TenantId, cancellationToken);
        
        if (session == null) throw new UnauthorizedAccessException("Chat Session not found or access denied.");

        var cacheKey = $"ChatHistory:{request.SessionId}";
        List<HistoryMessage>? historyMessages = null;
        var cachedHistory = await _cache.GetStringAsync(cacheKey, cancellationToken);
        
        if (!string.IsNullOrEmpty(cachedHistory))
        {
            try
            {
                historyMessages = JsonSerializer.Deserialize<List<HistoryMessage>>(cachedHistory);
            }
            catch { /* Ignore serialization errors and fallback to DB */ }
        }

        if (historyMessages == null)
        {
            // Load messages with absolute cap (prevents loading 10000+ messages)
            var dbMessages = await _dbContext.ChatMessages
                .Where(m => m.ChatSessionId == request.SessionId)
                .OrderByDescending(m => m.CreatedAt) // Load newest first
                .Take(MaxMessagesToLoad)
                .OrderBy(m => m.CreatedAt) // Then reorder chronologically
                .ToListAsync(cancellationToken);

            // Token management: apply sliding window with summary
            historyMessages = dbMessages.Select(m => new HistoryMessage
            {
                Role = m.Role,
                Content = m.Content,
                CreatedAt = m.CreatedAt
            }).ToList();
        }

        var trimResult = await _tokenManagement.TrimHistoryAsync(historyMessages, HistoryTokenBudget, cancellationToken);

        // Build ChatHistory with trimmed messages
        var history = new ChatHistory(model);
        foreach (var msg in trimResult.Messages)
        {
            if (msg.Role == "user") history.AddMessage(AuthorRole.User, msg.Content);
            else if (msg.Role == "assistant") history.AddMessage(AuthorRole.Assistant, msg.Content);
            else if (msg.Role == "system") history.AddMessage(AuthorRole.User, msg.Content); // Inject summary as user context
        }

        history.AddMessage(AuthorRole.User, request.Message);

        // Save user message
        var userMsg = new ChatMessage
        {
            ChatSessionId = request.SessionId,
            Role = "user",
            Content = request.Message,
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.ChatMessages.Add(userMsg);

        if (string.IsNullOrWhiteSpace(session.Title) || session.Title == "Đoạn chat mới")
        {
            session.Title = request.Message.Length > 35 
                ? request.Message.Substring(0, 35) + "..." 
                : request.Message;
        }

        // Store conversation summary for future reference
        if (trimResult.ConversationSummary != null)
        {
            session.Summary = trimResult.ConversationSummary;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var fullResponseBuilder = new System.Text.StringBuilder();

        await foreach (var text in _orchestrator.StreamProcessQueryAsync(session.TenantId, session.Id, request.UserId, request.Message, history, cancellationToken))
        {
            fullResponseBuilder.Append(text);
            yield return text;
        }

        var botMsg = new ChatMessage
        {
            ChatSessionId = request.SessionId,
            Role = "assistant",
            Content = fullResponseBuilder.ToString(),
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.ChatMessages.Add(botMsg);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Update cache with new messages
        historyMessages.Add(new HistoryMessage { Role = "user", Content = request.Message, CreatedAt = userMsg.CreatedAt });
        historyMessages.Add(new HistoryMessage { Role = "assistant", Content = botMsg.Content, CreatedAt = botMsg.CreatedAt });
        
        // Keep it to MaxMessagesToLoad for cache to avoid blowing up memory
        if (historyMessages.Count > MaxMessagesToLoad)
        {
            historyMessages = historyMessages.Skip(historyMessages.Count - MaxMessagesToLoad).ToList();
        }

        await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(historyMessages), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2)
        }, cancellationToken);
    }
}
