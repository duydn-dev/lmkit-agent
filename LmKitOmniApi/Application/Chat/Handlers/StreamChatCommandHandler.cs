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
    private readonly ILogger<StreamChatCommandHandler> _logger;

    // Maximum messages to load from DB (absolute cap)
    private const int MaxMessagesToLoad = 50;
    // Token budget for conversation history
    private const int HistoryTokenBudget = 3000;

    public StreamChatCommandHandler(
        LmModelManager modelManager,
        IAgentOrchestrator orchestrator,
        ITokenManagementService tokenManagement,
        HermesDbContext dbContext,
        IDistributedCache cache,
        ILogger<StreamChatCommandHandler> logger)
    {
        _modelManager = modelManager;
        _orchestrator = orchestrator;
        _tokenManagement = tokenManagement;
        _dbContext = dbContext;
        _cache = cache;
        _logger = logger;
    }

    public async IAsyncEnumerable<string> Handle(StreamChatCommand request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Intentionally tracked: this entity is mutated below (Title auto-generation,
        // Summary) and persisted via SaveChangesAsync — do not add AsNoTracking here.
        var session = await _dbContext.ChatSessions
            .FirstOrDefaultAsync(s => s.Id == request.SessionId
                && s.TenantId == request.TenantId
                && s.UserId == request.UserId,
                cancellationToken);

        if (session == null) throw new UnauthorizedAccessException("Chat Session not found or access denied.");

        var storedRole = await _dbContext.Users
            .Where(user => user.Id == request.UserId && user.TenantId == request.TenantId && user.IsActive)
            .Select(user => user.Role)
            .SingleOrDefaultAsync(cancellationToken);
        if (storedRole is null) throw new UnauthorizedAccessException("User is inactive or access was revoked.");
        var agentRole = storedRole.Equals("Admin", StringComparison.OrdinalIgnoreCase) ? "Admin" : "User";

        // Validate ownership before loading an expensive model. Model selection
        // is server-controlled; accepting a URL/model id from a chat request
        // would enable SSRF and disk/RAM exhaustion.
        var model = await _modelManager.GetChatModelAsync(ct: cancellationToken);

        var cacheKey = $"ChatHistory:{request.SessionId}";
        var isHistoryRewrite = request.Regenerate || request.ReplaceLastExchange;

        // The user turn actually sent to the model. For regenerate this becomes the
        // session's last stored user message; otherwise it is the incoming message.
        var effectiveMessage = request.Message;
        DateTime? regeneratedUserCreatedAt = null;
        DateTime? historyCutoffUtc = null;

        if (isHistoryRewrite)
        {
            var lastUserMessage = await _dbContext.ChatMessages
                .Where(m => m.ChatSessionId == request.SessionId && m.Role == "user")
                .OrderByDescending(m => m.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (request.Regenerate && lastUserMessage == null)
            {
                // Nothing to re-run; surface a friendly notice instead of an exception.
                yield return "[Không có tin nhắn nào để tạo lại]";
                yield break;
            }

            if (lastUserMessage != null)
            {
                // Drop every assistant reply produced after the last user turn; for
                // edit-last (ReplaceLastExchange) also drop that user turn itself.
                var trailingAssistantMessages = await _dbContext.ChatMessages
                    .Where(m => m.ChatSessionId == request.SessionId
                        && m.Role == "assistant"
                        && m.CreatedAt > lastUserMessage.CreatedAt)
                    .ToListAsync(cancellationToken);
                _dbContext.ChatMessages.RemoveRange(trailingAssistantMessages);

                if (request.ReplaceLastExchange)
                {
                    _dbContext.ChatMessages.Remove(lastUserMessage);
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            // Invalidate the cached history BEFORE building it so the deleted
            // exchange can never be replayed from cache; the history is re-read
            // fresh from the DB (post-deletion) below.
            await _cache.RemoveAsync(cacheKey, cancellationToken);

            if (request.Regenerate)
            {
                effectiveMessage = lastUserMessage!.Content;
                regeneratedUserCreatedAt = lastUserMessage.CreatedAt;
                // The re-run user turn is passed to the orchestrator as the query,
                // so exclude it (and anything after it) from the loaded history —
                // mirroring a normal send, where the pending message is not yet stored.
                historyCutoffUtc = lastUserMessage.CreatedAt;
            }
        }

        List<HistoryMessage>? cachedMessages = null;
        if (!isHistoryRewrite)
        {
            var cachedHistory = await _cache.GetStringAsync(cacheKey, cancellationToken);
            if (!string.IsNullOrEmpty(cachedHistory))
            {
                try
                {
                    cachedMessages = JsonSerializer.Deserialize<List<HistoryMessage>>(cachedHistory);
                }
                catch { /* Ignore serialization errors and fallback to DB */ }
            }
        }

        List<HistoryMessage> historyMessages;
        if (cachedMessages != null)
        {
            historyMessages = cachedMessages;
        }
        else
        {
            // Load messages with absolute cap (prevents loading 10000+ messages).
            // Read-only: rows are only copied into HistoryMessage below, never
            // updated, so skip change tracking.
            var messagesQuery = _dbContext.ChatMessages
                .AsNoTracking()
                .Where(m => m.ChatSessionId == request.SessionId);

            if (historyCutoffUtc.HasValue)
            {
                var cutoffUtc = historyCutoffUtc.Value;
                messagesQuery = messagesQuery.Where(m => m.CreatedAt < cutoffUtc);
            }

            var dbMessages = await messagesQuery
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

        // Save user message. Regenerate re-runs the already stored last user turn,
        // so no new user row is inserted in that mode.
        ChatMessage? userMsg = null;
        if (!request.Regenerate)
        {
            userMsg = new ChatMessage
            {
                ChatSessionId = request.SessionId,
                Role = "user",
                Content = request.Message,
                CreatedAt = DateTime.UtcNow
            };
            _dbContext.ChatMessages.Add(userMsg);
        }

        if (string.IsNullOrWhiteSpace(session.Title) || session.Title == CreateChatSessionCommand.DefaultChatTitle)
        {
            session.Title = effectiveMessage.Length > 35
                ? effectiveMessage.Substring(0, 35) + "..."
                : effectiveMessage;
        }

        // Store conversation summary for future reference
        if (trimResult.ConversationSummary != null)
        {
            session.Summary = trimResult.ConversationSummary;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Timestamp of the user turn this response answers (new row, or the
        // re-run last user message when regenerating) — used for the cache entry.
        var userTurnCreatedAt = userMsg?.CreatedAt ?? regeneratedUserCreatedAt!.Value;

        var options = new AgentRequestOptions { AllowWebSearch = request.EnableWebSearch };

        var fullResponseBuilder = new System.Text.StringBuilder();
        ChatMessage? botMsg = null;
        var assistantPersisted = false;

        // Creates (at most once) and flushes the assistant row. Shared by the
        // success path and the cancellation path in the finally below, so a
        // SaveChanges interrupted by cancellation cannot double-insert the row.
        async Task<ChatMessage> PersistAssistantMessageAsync(CancellationToken ct)
        {
            if (botMsg == null)
            {
                botMsg = new ChatMessage
                {
                    ChatSessionId = request.SessionId,
                    Role = "assistant",
                    Content = fullResponseBuilder.ToString(),
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.ChatMessages.Add(botMsg);
            }
            await _dbContext.SaveChangesAsync(ct);
            return botMsg;
        }

        // Update cache with new messages
        async Task WriteHistoryCacheAsync(ChatMessage assistantMessage, CancellationToken ct)
        {
            historyMessages.Add(new HistoryMessage { Role = "user", Content = effectiveMessage, CreatedAt = userTurnCreatedAt });
            historyMessages.Add(new HistoryMessage { Role = "assistant", Content = assistantMessage.Content, CreatedAt = assistantMessage.CreatedAt });

            // Keep it to MaxMessagesToLoad for cache to avoid blowing up memory
            if (historyMessages.Count > MaxMessagesToLoad)
            {
                historyMessages = historyMessages.Skip(historyMessages.Count - MaxMessagesToLoad).ToList();
            }

            await _cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(historyMessages), new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2)
            }, ct);
        }

        // yield-ing is only legal inside try/finally (not try/catch); the finally is
        // what preserves a partial answer when the client aborts mid-stream: the
        // controller's await-foreach disposes this enumerator, disposal runs the
        // finally within the still-alive request scope (scoped DbContext included).
        try
        {
            await foreach (var text in _orchestrator.StreamProcessQueryAsync(session.TenantId, session.Id, request.UserId, agentRole, effectiveMessage, history, options, cancellationToken))
            {
                fullResponseBuilder.Append(text);
                yield return text;
            }

            var savedAssistant = await PersistAssistantMessageAsync(cancellationToken);
            assistantPersisted = true;

            await WriteHistoryCacheAsync(savedAssistant, cancellationToken);
        }
        finally
        {
            if (!assistantPersisted && fullResponseBuilder.Length > 0 && cancellationToken.IsCancellationRequested)
            {
                // Best-effort: persist whatever was generated before the stop, using
                // CancellationToken.None because the request token is already canceled.
                try
                {
                    var savedAssistant = await PersistAssistantMessageAsync(CancellationToken.None);
                    await WriteHistoryCacheAsync(savedAssistant, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist the partial assistant response for chat session {SessionId} after cancellation.", request.SessionId);
                }
            }
        }
    }
}
