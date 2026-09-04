using LmKitOmniApi.Application.Projects;
using LmKitOmniApi.Application.UserPreferences;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pure, model-free tests for the custom-instructions composition + validation used
/// by the custom-instructions endpoint AND by StreamChatCommandHandler.BuildAgentOptionsAsync
/// to build AgentRequestOptions.PersonaPrompt. Proves the ChatGPT-style user persona
/// is PREPENDED so a bound custom-agent/project persona still applies after it.
/// </summary>
public sealed class UserPreferenceRulesTests
{
    [Fact]
    public void Compose_WhenBothFieldsEmpty_ReturnsExistingPersonaUnchanged()
    {
        // No custom instructions must be byte-identical to today's behavior.
        Assert.Null(UserPreferenceRules.ComposePersonaPrompt(null, null, null));
        Assert.Null(UserPreferenceRules.ComposePersonaPrompt("   ", "\t", null));
        Assert.Equal("Agent persona", UserPreferenceRules.ComposePersonaPrompt(null, "  ", "Agent persona"));
    }

    [Fact]
    public void Compose_WithOnlyAboutUser_EmitsAboutSectionOnly()
    {
        var result = UserPreferenceRules.ComposePersonaPrompt("Tôi là kỹ sư phần mềm.", null, null);

        Assert.NotNull(result);
        Assert.Contains("## Hướng dẫn tùy chỉnh của người dùng", result);
        Assert.Contains("### Thông tin về người dùng", result);
        Assert.Contains("Tôi là kỹ sư phần mềm.", result);
        Assert.DoesNotContain("### Phong cách phản hồi mong muốn", result);
    }

    [Fact]
    public void Compose_WithOnlyResponseStyle_EmitsStyleSectionOnly()
    {
        var result = UserPreferenceRules.ComposePersonaPrompt(null, "Trả lời ngắn gọn.", null);

        Assert.NotNull(result);
        Assert.Contains("### Phong cách phản hồi mong muốn", result);
        Assert.Contains("Trả lời ngắn gọn.", result);
        Assert.DoesNotContain("### Thông tin về người dùng", result);
    }

    [Fact]
    public void Compose_WithExistingPersona_PrependsCustomInstructionsBeforeIt()
    {
        const string persona = "## Persona của agent\nBạn là trợ lý pháp lý.";
        var result = UserPreferenceRules.ComposePersonaPrompt("Tôi ở Hà Nội.", "Luôn dùng tiếng Việt.", persona)!;

        var customIndex = result.IndexOf("Hướng dẫn tùy chỉnh của người dùng", StringComparison.Ordinal);
        var personaIndex = result.IndexOf("Persona của agent", StringComparison.Ordinal);

        Assert.True(customIndex >= 0 && personaIndex >= 0);
        Assert.True(customIndex < personaIndex, "Custom instructions must be prepended BEFORE the bound persona.");
        Assert.Contains("Tôi ở Hà Nội.", result);
        Assert.Contains("Bạn là trợ lý pháp lý.", result);
    }

    [Fact]
    public void Compose_LayersCustomInstructionsThenProjectThenAgent_MatchingHandlerOrder()
    {
        // Mirror BuildAgentOptionsAsync exactly: agent persona → project prepended →
        // custom instructions prepended. The final order must be custom → project → agent.
        var afterProject = ProjectRules.ComposePersonaPrompt("Hướng dẫn của dự án X.", "Bạn là trợ lý của dự án.");
        var final = UserPreferenceRules.ComposePersonaPrompt("Về tôi.", "Phong cách của tôi.", afterProject)!;

        var customIndex = final.IndexOf("Hướng dẫn tùy chỉnh của người dùng", StringComparison.Ordinal);
        var projectIndex = final.IndexOf("Hướng dẫn dự án", StringComparison.Ordinal);
        var agentIndex = final.IndexOf("Persona của agent", StringComparison.Ordinal);

        Assert.True(customIndex >= 0 && projectIndex >= 0 && agentIndex >= 0);
        Assert.True(customIndex < projectIndex, "Custom instructions must precede project instructions.");
        Assert.True(projectIndex < agentIndex, "Project instructions must precede the agent persona.");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("ok", "ok")]
    public void Validate_AcceptsNullAndWithinLimit(string? about, string? style)
    {
        Assert.Null(UserPreferenceRules.Validate(about, style));
        Assert.Null(UserPreferenceRules.Validate(new string('a', 2000), new string('b', 2000)));
    }

    [Fact]
    public void Validate_RejectsOverlongFieldsWithVietnameseMessages()
    {
        Assert.Equal(
            "Thông tin về bạn không được vượt quá 2000 ký tự.",
            UserPreferenceRules.Validate(new string('a', 2001), null));
        Assert.Equal(
            "Phong cách phản hồi không được vượt quá 2000 ký tự.",
            UserPreferenceRules.Validate(null, new string('b', 2001)));
    }

    [Fact]
    public void NormalizeOptional_TrimsAndCollapsesWhitespaceToNull()
    {
        Assert.Null(UserPreferenceRules.NormalizeOptional(null));
        Assert.Null(UserPreferenceRules.NormalizeOptional("   "));
        Assert.Equal("hello", UserPreferenceRules.NormalizeOptional("  hello  "));
    }
}
