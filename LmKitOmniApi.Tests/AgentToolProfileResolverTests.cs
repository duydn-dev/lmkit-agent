using LmKitOmniApi.Infrastructure.AI.Tools;

namespace LmKitOmniApi.Tests;

public class AgentToolProfileResolverTests
{
    [Fact]
    public void NormalChat_OnlyExposesSafeProfile() =>
        Assert.Equal(AgentToolProfile.SafeChat, AgentToolProfileResolver.Resolve("Giải thích dependency injection"));

    [Theory]
    [InlineData("tin mới nhất hôm nay")]
    [InlineData("search the web for the current release")]
    public void CurrentInformation_AddsResearch(string query) =>
        Assert.True(AgentToolProfileResolver.Resolve(query).HasFlag(AgentToolProfile.Research));

    [Fact]
    public void ExplicitResources_AddOnlyRelevantCapabilities()
    {
        var profile = AgentToolProfileResolver.Resolve("analyze Uploads/a.png and connect MCP integration");
        Assert.True(profile.HasFlag(AgentToolProfile.ImageRead));
        Assert.True(profile.HasFlag(AgentToolProfile.ExternalMcp));
        Assert.False(profile.HasFlag(AgentToolProfile.AudioRead));
    }
}
