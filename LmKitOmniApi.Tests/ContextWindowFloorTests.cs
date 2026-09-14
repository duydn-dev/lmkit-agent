using System.Text.Json;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Services;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pins the token-window floor that keeps answers from being cut off mid-sentence, and the
/// detector that names a truncated turn when it still happens.
/// </summary>
/// <remarks>
/// Regression test for a live failure. LM-Kit sized the chat conversation to a 2048-token window
/// on this machine; the Vietnamese synthesis prompt alone claimed ~1771 of those, and the
/// answering pass was cut inside a URL after 467 characters — with the "Đã đọc N trang web" chip
/// rendered normally above it, so the turn looked successful. The window floor
/// (<c>AiModels:MinContextSize</c>) is what fixes it; a floor set too low silently restores the
/// bug, so the range rule is asserted here rather than left to configuration review.
/// </remarks>
public sealed class ContextWindowFloorTests
{
    [Fact]
    public void Unset_FloorFallsBackToTheShippedDefault()
    {
        Assert.Equal(LmModelManager.DefaultMinContextSize, LmModelManager.ResolveMinContextSize(null));
    }

    [Theory]
    [InlineData(2048)]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(16384)]
    [InlineData(32768)]
    public void AConfiguredFloor_IsHonouredVerbatim(int configured)
    {
        Assert.Equal(configured, LmModelManager.ResolveMinContextSize(configured));
    }

    /// <summary>
    /// The floor that actually ships is the one in <c>appsettings.json</c> (it is set explicitly, so
    /// <see cref="LmModelManager.DefaultMinContextSize"/> only covers hosts that omit the key). A
    /// value that the resolver refuses throws at startup, and a value below what was measured
    /// sufficient quietly restores the truncation — so the shipped file is checked here rather
    /// than trusted, because neither failure looks like a configuration error from the outside.
    /// </summary>
    [Fact]
    public void TheShippedFloor_PassesTheResolversOwnRules()
    {
        var shipped = ReadShippedMinContextSize();

        Assert.NotNull(shipped);
        Assert.Equal(shipped, LmModelManager.ResolveMinContextSize(shipped));
        Assert.True(
            shipped >= LmModelManager.MeasuredSufficientContextSize,
            $"AiModels:MinContextSize ships as {shipped}, below the measured-sufficient "
            + $"{LmModelManager.MeasuredSufficientContextSize}; answers were truncated mid-sentence there.");
    }

    /// <summary>Reads <c>AiModels:MinContextSize</c> from the shipped appsettings.json, or null when absent.</summary>
    private static int? ReadShippedMinContextSize()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory is not null; depth++)
        {
            var candidate = Path.Combine(directory.FullName, "LmKitOmniApi", "appsettings.json");
            if (File.Exists(candidate))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(candidate));
                if (document.RootElement.TryGetProperty("AiModels", out var aiModels)
                    && aiModels.TryGetProperty("MinContextSize", out var floor)
                    && floor.TryGetInt32(out var value))
                {
                    return value;
                }

                return null;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// A window this small cannot hold the system prompt plus an answer, which is the truncation
    /// the floor exists to prevent. Refusing it loudly at startup beats serving half answers.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(256)]
    [InlineData(1024)]
    [InlineData(2047)]
    public void AFloorTooSmallToFitAPromptAndAnAnswer_IsRefusedWithTheReason(int tooSmall)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => LmModelManager.ResolveMinContextSize(tooSmall));

        Assert.Contains("AiModels:MinContextSize", error.Message, StringComparison.Ordinal);
        Assert.Contains("truncates answers", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFloorBeyondWhatADevBoxCanServe_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(
            () => LmModelManager.ResolveMinContextSize(LmModelManager.MaxAllowedContextSize + 1));
    }

    /// <summary>
    /// The value must actually reach LM-Kit — the setting is inert otherwise, and the failure it
    /// prevents (a silently undersized window) looks exactly like a healthy configuration.
    /// </summary>
    [Fact]
    public void ApplyingTheFloor_SetsTheLmKitGlobalThatSizesConversations()
    {
        var previous = LMKit.Global.Configuration.MinContextSize;
        try
        {
            Assert.Equal(4096, LmModelManager.ApplyMinContextSize(4096));
            Assert.Equal(4096, LMKit.Global.Configuration.MinContextSize);
        }
        finally
        {
            LMKit.Global.Configuration.MinContextSize = previous;
        }
    }

    /// <summary>
    /// 1 is the measured remainder of a turn that ran out of window; 1917 is a turn that finished
    /// on its own. The detector has to separate those two, not merely exist.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(8, true)]
    [InlineData(9, false)]
    [InlineData(337, false)]
    [InlineData(1917, false)]
    public void TheTruncationDetector_SeparatesAWindowedStopFromAFinishedAnswer(
        int contextRemaining, bool truncated)
    {
        Assert.Equal(truncated, AgentOrchestrator.IsAnswerLikelyTruncated(contextRemaining));
    }
}
