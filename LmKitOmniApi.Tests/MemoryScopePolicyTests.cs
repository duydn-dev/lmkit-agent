using LmKitOmniApi.Infrastructure.AI;

namespace LmKitOmniApi.Tests;

public class MemoryScopePolicyTests
{
    [Fact]
    public void UserMemory_IsVisibleOnlyToOwner()
    {
        var owner = Guid.NewGuid();
        Assert.True(MemoryScopePolicy.CanRecall(owner, owner));
        Assert.False(MemoryScopePolicy.CanRecall(owner, Guid.NewGuid()));
        Assert.False(MemoryScopePolicy.CanRecall(owner, null));
    }

    [Fact]
    public void TenantSharedMemory_IsVisibleWithinTenantQuery()
    {
        Assert.True(MemoryScopePolicy.CanRecall(null, Guid.NewGuid()));
        Assert.True(MemoryScopePolicy.CanRecall(null, null));
    }
}
