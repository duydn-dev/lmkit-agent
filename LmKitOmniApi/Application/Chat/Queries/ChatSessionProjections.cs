namespace LmKitOmniApi.Application.Chat.Queries;

/// <summary>
/// Single source of truth for the <see cref="ChatSessionDto"/> projection shared by
/// GetChatSessionsQueryHandler, GetChatSessionsSearchQueryHandler (its empty-query
/// and search branches) and GetProjectSessionsQueryHandler. Exposed as an
/// <see cref="System.Linq.Expressions.Expression{TDelegate}"/> — not a compiled
/// <see cref="System.Func{T, TResult}"/> — so EF Core translates it to SQL instead
/// of falling back to client-side evaluation.
/// </summary>
public static class ChatSessionProjections
{
    public static readonly System.Linq.Expressions.Expression<System.Func<Domain.Entities.ChatSession, ChatSessionDto>> ToDto =
        s => new ChatSessionDto
        {
            Id = s.Id,
            Title = s.Title,
            CreatedAt = s.CreatedAt,
            CustomAgentId = s.CustomAgentId,
            AgentName = s.CustomAgent != null ? s.CustomAgent.Name : null,
            AgentIcon = s.CustomAgent != null ? s.CustomAgent.Icon : null,
            ProjectId = s.ProjectId
        };
}
