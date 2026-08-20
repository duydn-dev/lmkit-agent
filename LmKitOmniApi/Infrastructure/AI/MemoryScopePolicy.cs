namespace LmKitOmniApi.Infrastructure.AI;

public static class MemoryScopePolicy
{
    public static bool CanRecall(Guid? memoryUserId, Guid? requestingUserId) =>
        memoryUserId is null || memoryUserId == requestingUserId;
}
