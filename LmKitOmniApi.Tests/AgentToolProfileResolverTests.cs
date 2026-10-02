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
    // Live turn: the URL carries no trigger keyword of its own, so the pasted link itself has to
    // classify the request — the exact query that came back as "Tôi không thể truy cập nội dung từ
    // đường link bạn cung cấp".
    [InlineData("https://laodong.vn/kinh-doanh/gia-xang-dau-hom-nay-210-tang-manh-hon-4-1776264.ldo đấy nhé, tạo báo cáo giá xăng dầu xem nào")]
    [InlineData("tóm tắt giúp tôi trang này https://example.org/bao-cao")]
    [InlineData("đọc đường dẫn này rồi viết báo cáo")]
    public void CurrentInformation_AddsResearch(string query) =>
        Assert.True(AgentToolProfileResolver.Resolve(query).HasFlag(AgentToolProfile.Research));

    /// <summary>
    /// The counter-case for the URL trigger: a bare URL must not drag in the profiles it has no
    /// business claiming. A pasted link is research, not an image or audio attachment.
    /// </summary>
    [Fact]
    public void APastedLink_DoesNotClaimUnrelatedProfiles()
    {
        var profile = AgentToolProfileResolver.Resolve("https://example.org/bao-cao-quy-3");

        Assert.True(profile.HasFlag(AgentToolProfile.Research));
        Assert.False(profile.HasFlag(AgentToolProfile.ImageRead));
        Assert.False(profile.HasFlag(AgentToolProfile.AudioRead));
        Assert.False(profile.HasFlag(AgentToolProfile.ExternalMcp));
    }

    [Fact]
    public void ExplicitResources_AddOnlyRelevantCapabilities()
    {
        var profile = AgentToolProfileResolver.Resolve("analyze Uploads/a.png and connect MCP integration");
        Assert.True(profile.HasFlag(AgentToolProfile.ImageRead));
        Assert.True(profile.HasFlag(AgentToolProfile.ExternalMcp));
        Assert.False(profile.HasFlag(AgentToolProfile.AudioRead));
    }
}
