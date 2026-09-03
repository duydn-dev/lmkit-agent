using System.Runtime.CompilerServices;
using MediatR;
using Microsoft.EntityFrameworkCore;
using LMKit.TextGeneration;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Application.Chat.Commands;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.CustomAgents;
using LmKitOmniApi.Application.Projects;
using LmKitOmniApi.Services;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Domain.Entities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LmKitOmniApi.Application.Chat.Handlers;

public class StreamChatCommandHandler : IStreamRequestHandler<StreamChatCommand, string>
{
    private readonly LmModelManager _modelManager;
    private readonly IAgentOrchestrator _orchestrator;
    private readonly ITokenManagementService _tokenManagement;
    private readonly HermesDbContext _dbContext;
    private readonly IDistributedCache _cache;
    private readonly ChatReasoningOptions _reasoning;
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
        IOptions<ChatReasoningOptions> reasoningOptions,
        ILogger<StreamChatCommandHandler> logger)
    {
        _modelManager = modelManager;
        _orchestrator = orchestrator;
        _tokenManagement = tokenManagement;
        _dbContext = dbContext;
        _cache = cache;
        _reasoning = reasoningOptions.Value;
        _logger = logger;
    }

    // The two mutually-exclusive wire flags (StreamChatCommand.Regenerate /
    // ReplaceLastExchange, which the controller rejects with a 400 when both are
    // set) collapse to a single internal mode, so the invalid both-set state is
    // never representable past the top of Handle.
    private enum ChatSendMode
    {
        // Normal send: store the incoming message and answer it.
        Normal,
        // Re-run the session's last user turn; the incoming message is ignored.
        Regenerate,
        // Edit-last: drop the last exchange, then send like Normal.
        EditLast
    }

    // Small result of the regenerate/edit-last history rewrite handed back to
    // Handle, which owns the iterator-only "nothing to regenerate" early return.
    private readonly record struct ResendRewrite(
        bool NothingToRegenerate,
        string EffectiveMessage,
        DateTime? RegeneratedUserCreatedAt,
        DateTime? HistoryCutoffUtc);

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

        // Collapse the two mutually-exclusive wire flags into one internal mode
        // (the controller 400s the both-set combination before we get here), so the
        // history-rewrite logic below is driven off `mode` alone.
        var mode = request.Regenerate ? ChatSendMode.Regenerate
            : request.ReplaceLastExchange ? ChatSendMode.EditLast
            : ChatSendMode.Normal;

        // The user turn actually sent to the model. For regenerate this becomes the
        // session's last stored user message; otherwise it is the incoming message.
        var effectiveMessage = request.Message;
        DateTime? regeneratedUserCreatedAt = null;
        DateTime? historyCutoffUtc = null;

        if (mode != ChatSendMode.Normal)
        {
            var rewrite = await RewriteHistoryForResendAsync(request, mode, cacheKey, cancellationToken);

            if (rewrite.NothingToRegenerate)
            {
                // Nothing to re-run; surface a friendly notice instead of an exception.
                yield return "[Không có tin nhắn nào để tạo lại]";
                yield break;
            }

            effectiveMessage = rewrite.EffectiveMessage;
            regeneratedUserCreatedAt = rewrite.RegeneratedUserCreatedAt;
            historyCutoffUtc = rewrite.HistoryCutoffUtc;
        }

        var (historyMessages, trimResult) = await LoadHistoryAsync(request, mode, cacheKey, historyCutoffUtc, cancellationToken);

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
        if (mode != ChatSendMode.Regenerate)
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

        var options = await BuildAgentOptionsAsync(request, session, cancellationToken);

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
            if (!assistantPersisted && cancellationToken.IsCancellationRequested
                && !string.IsNullOrWhiteSpace(StripProtocolMarkers(fullResponseBuilder.ToString())))
            {
                // Best-effort: persist whatever was generated before the stop, using
                // CancellationToken.None because the request token is already canceled.
                // Guarded on the STRIPPED view so a stop after only status markers
                // ([THINKING]/[WEB_SEARCH]/[Agent invoked]) with no real answer does not
                // persist a marker-only row that renders as a permanent blank bubble.
                // The row that IS stored keeps the original markers verbatim
                // (PersistAssistantMessageAsync stores fullResponseBuilder.ToString()),
                // so the history loader can still re-extract the thinking steps.
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
            else if (!assistantPersisted && !cancellationToken.IsCancellationRequested)
            {
                // Mid-stream error (non-cancellation) with nothing persisted. The user
                // turn was committed to the DB before streaming, but the 2h history cache
                // still holds the pre-send snapshot that predates it — and the next send
                // prefers that cache over the DB, silently losing this turn for up to 2h.
                // Best-effort drop the cache key so the next send rebuilds from the DB
                // (which has the committed user turn). Never throw from the finally.
                try
                {
                    await _cache.RemoveAsync(cacheKey, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to invalidate the history cache for chat session {SessionId} after a mid-stream error.", request.SessionId);
                }
            }
        }
    }

    // ── Regenerate / edit-last history rewrite ────────────────────────────
    // Finds the session's last user turn and drops the trailing assistant replies
    // (and, for edit-last, that user turn too), flushes the deletes, then
    // invalidates the cached history BEFORE it is rebuilt so the removed exchange
    // can never be replayed from cache. Returns the effective user message and the
    // timestamps that drive the reload. In Regenerate mode with no user turn to
    // re-run it returns NothingToRegenerate without touching the DB or cache, and
    // Handle (the async iterator) emits the friendly notice and stops.
    private async Task<ResendRewrite> RewriteHistoryForResendAsync(
        StreamChatCommand request,
        ChatSendMode mode,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var lastUserMessage = await _dbContext.ChatMessages
            .Where(m => m.ChatSessionId == request.SessionId && m.Role == "user")
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (mode == ChatSendMode.Regenerate && lastUserMessage == null)
        {
            // Nothing to re-run; Handle surfaces a friendly notice instead of an exception.
            return new ResendRewrite(true, request.Message, null, null);
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

            if (mode == ChatSendMode.EditLast)
            {
                _dbContext.ChatMessages.Remove(lastUserMessage);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        // Invalidate the cached history BEFORE building it so the deleted
        // exchange can never be replayed from cache; the history is re-read
        // fresh from the DB (post-deletion) by LoadHistoryAsync.
        await _cache.RemoveAsync(cacheKey, cancellationToken);

        var effectiveMessage = request.Message;
        DateTime? regeneratedUserCreatedAt = null;
        DateTime? historyCutoffUtc = null;

        if (mode == ChatSendMode.Regenerate)
        {
            effectiveMessage = lastUserMessage!.Content;
            regeneratedUserCreatedAt = lastUserMessage.CreatedAt;
            // The re-run user turn is passed to the orchestrator as the query,
            // so exclude it (and anything after it) from the loaded history —
            // mirroring a normal send, where the pending message is not yet stored.
            historyCutoffUtc = lastUserMessage.CreatedAt;
        }

        return new ResendRewrite(false, effectiveMessage, regeneratedUserCreatedAt, historyCutoffUtc);
    }

    // ── Conversation-history load ─────────────────────────────────────────
    // Prefers the 2h cache snapshot for a normal send; a regenerate/edit-last
    // request (whose cache RewriteHistoryForResendAsync already invalidated) and a
    // cache miss both fall through to a capped, read-only DB read honouring the
    // optional regenerate cutoff. The sliding-window token trim runs last. Returns
    // the raw history list (the cache writer extends it after streaming) alongside
    // the trim result.
    private async Task<(List<HistoryMessage> HistoryMessages, TrimmedHistoryResult TrimResult)> LoadHistoryAsync(
        StreamChatCommand request,
        ChatSendMode mode,
        string cacheKey,
        DateTime? historyCutoffUtc,
        CancellationToken cancellationToken)
    {
        List<HistoryMessage>? cachedMessages = null;
        if (mode == ChatSendMode.Normal)
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
        return (historyMessages, trimResult);
    }

    // ── Custom-agent + project request options ────────────────────────────
    // Composes the per-request AgentRequestOptions from the session's bound custom
    // agent (persona / tool whitelist / knowledge scope, re-validated as still
    // visible to the caller) and then the enclosing project's instructions
    // (prepended to the persona). A missing/foreign/deleted agent or project leaves
    // the corresponding layer untouched, so an unbound, project-less session yields
    // byte-identical options to an agentless request. The agent and project reads
    // stay two round-trips: they hit different tables under independent presence
    // guards, and the project composition consumes the agent's persona.
    private async Task<AgentRequestOptions> BuildAgentOptionsAsync(
        StreamChatCommand request,
        ChatSession session,
        CancellationToken cancellationToken)
    {
        // ── Custom-agent options (Gems-style persona/tool/knowledge scope) ──
        // A bound agent must still be visible to the caller (owner or tenant-shared);
        // if it is not (unshared or deleted since binding), the request proceeds
        // WITHOUT it — byte-identical to an unbound session.
        CustomAgent? customAgent = null;
        if (session.CustomAgentId is Guid customAgentId)
        {
            customAgent = await _dbContext.CustomAgents
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == customAgentId
                    && a.TenantId == request.TenantId
                    && (a.OwnerUserId == request.UserId || a.IsSharedWithTenant),
                    cancellationToken);
        }

        AgentRequestOptions options;
        if (customAgent is null)
        {
            options = new AgentRequestOptions { AllowWebSearch = request.EnableWebSearch };
        }
        else
        {
            var allowedTools = CustomAgentRules.ParseToolsCsv(customAgent.AllowedToolsCsv);
            options = new AgentRequestOptions
            {
                // The user-facing toggle composes with the agent whitelist: web
                // search runs only when the request enables it AND the agent
                // either has no whitelist or whitelists SearchWeb.
                AllowWebSearch = request.EnableWebSearch
                    && (allowedTools is null || allowedTools.Contains("SearchWeb", StringComparer.OrdinalIgnoreCase)),
                PersonaPrompt = customAgent.PersonaPrompt,
                AllowedTools = allowedTools,
                KnowledgeDocumentIds = CustomAgentRules.ParseDocumentIdsCsv(customAgent.KnowledgeDocumentIdsCsv)
            };
        }

        // Operator-gated DeepSeek-R1-style reasoning display (a `with` so the later
        // project-instructions composition preserves it).
        options = options with { ShowReasoning = _reasoning.Enabled };

        // ── Project instructions (ChatGPT-Projects style shared context) ──
        // A session inside a project prepends the project's instructions to the
        // persona prompt. Tenant+owner scoped; a missing/foreign project or
        // empty/whitespace instructions means the request proceeds WITHOUT them —
        // and with no project anywhere, `options` is left untouched, so behavior
        // stays byte-identical to a project-less session.
        if (session.ProjectId is Guid sessionProjectId)
        {
            var projectInstructions = await _dbContext.Projects
                .AsNoTracking()
                .Where(p => p.Id == sessionProjectId
                    && p.TenantId == request.TenantId
                    && p.UserId == request.UserId)
                .Select(p => p.Instructions)
                .FirstOrDefaultAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(projectInstructions))
            {
                options = options with
                {
                    PersonaPrompt = ProjectRules.ComposePersonaPrompt(projectInstructions, options.PersonaPrompt)
                };
            }
        }

        return options;
    }

    // ── Orchestrator status-marker stripping ──────────────────────────────
    // The orchestrator interleaves status markers into the streamed/persisted
    // assistant body — "[Agent invoked: ...]", "[THINKING]: ..." and
    // "[WEB_SEARCH]: ..." lines — which the frontend strips back out for display
    // (parseStoredAssistantContent in src/composables/useChatStream.ts). These
    // patterns mirror those regexes so we can tell a real answer apart from a
    // marker-only response. This stripped view decides ONLY whether to persist;
    // the value actually stored keeps the original markers so the history loader
    // can re-extract the thinking steps and web references.
    private static readonly Regex AgentInvokedMarker =
        new Regex(@"\[Agent invoked:.*?\][\n\r]*", RegexOptions.Compiled);
    private static readonly Regex ThinkingMarker =
        new Regex(@"\[THINKING\]:[^\n\r]+[\n\r]*", RegexOptions.Compiled);
    private static readonly Regex WebSearchMarker =
        new Regex(@"\[WEB_SEARCH\]:[^\n\r]+[\n\r]*", RegexOptions.Compiled);

    private static readonly Regex ReasoningMarker =
        new Regex(@"\[REASONING\]:[^\n\r]+[\n\r]*", RegexOptions.Compiled);

    private static string StripProtocolMarkers(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var stripped = AgentInvokedMarker.Replace(raw, string.Empty);
        stripped = ThinkingMarker.Replace(stripped, string.Empty);
        stripped = WebSearchMarker.Replace(stripped, string.Empty);
        stripped = ReasoningMarker.Replace(stripped, string.Empty);
        return stripped;
    }
}
