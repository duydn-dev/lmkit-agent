using LmKitOmniApi.Infrastructure.AI;

namespace LmKitOmniApi.Tests;

public class MemoryRetentionPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Session", 1)]
    [InlineData("ShortTerm", 1)]
    [InlineData("Episodic", 90)]
    [InlineData("Fact", 180)]
    [InlineData("unknown", 90)]
    public void TransientMemory_HasBoundedRetention(string type, int expectedDays)
    {
        Assert.Equal(Now.AddDays(expectedDays), MemoryRetentionPolicy.GetDefaultExpiration(type, Now));
    }

    [Theory]
    [InlineData("UserProfile")]
    [InlineData("Preference")]
    [InlineData("Semantic")]
    public void DurableMemory_RequiresExplicitDeletion(string type)
    {
        Assert.Null(MemoryRetentionPolicy.GetDefaultExpiration(type, Now));
    }
}
