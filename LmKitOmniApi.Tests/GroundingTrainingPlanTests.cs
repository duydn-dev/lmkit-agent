using LMKit.Inference;
using LmKitOmniApi.Infrastructure.AI.ComputerUse;
using LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;

namespace LmKitOmniApi.Tests;

/// <summary>
/// CI tests for the model-free half of grounding fine-tuning: how a captured
/// <see cref="GroundingSample"/> becomes a training turn, whether the SCREENSHOT the decision
/// was actually made from survives into the sample, and whether the training prompt matches the
/// inference prompt.
///
/// This is the part the shipped pipeline had no coverage of at all — it built
/// <c>TaskGoal + "\n" + ElementsText</c>, never looked at the recorded screenshot, and never
/// tagged a modality, so every "grounding" sample was text-only and shaped unlike anything the
/// live model is ever asked.
/// </summary>
public sealed class GroundingTrainingPlanTests
{
    private static GroundingSample Sample(Guid tenantId, string? screenshotFileId = null) => new()
    {
        TenantId = tenantId,
        TaskGoal = "Find the pricing page",
        PageUrl = "https://example.com/",
        ElementsText = "INTERACTIVE ELEMENTS (address these by 'ref'):\n  [1] button: OK\n",
        ScreenshotFileId = screenshotFileId,
        SystemPrompt = "SYSTEM",
        CorrectActionJson = "{\"action\":\"click\",\"ref\":1}",
    };

    // ── 1. Modality: a resolvable screenshot makes the sample MULTIMODAL ──

    [Fact]
    public void Build_TagsMultimodal_AndCarriesTheScreenshot_WhenTheFileResolves()
    {
        var tenantId = Guid.NewGuid();
        var plan = GroundingTrainingPlan.Build(
            [Sample(tenantId, "shot.png")],
            (_, id) => id is null ? null : @"C:\uploads\t\u\shot.png");

        var turn = Assert.Single(plan.Turns);
        Assert.Equal(@"C:\uploads\t\u\shot.png", turn.ScreenshotPath);
        Assert.Equal(InferenceModality.Multimodal, turn.Modality);
        Assert.Equal(1, plan.WithScreenshot);
        Assert.Equal(0, plan.WithoutScreenshot);
        Assert.Equal(0, plan.MissingScreenshotFile);
    }

    [Fact]
    public void Build_TagsText_WhenTheSampleNeverHadAScreenshot()
    {
        var plan = GroundingTrainingPlan.Build([Sample(Guid.NewGuid())], (_, _) => "never-called");

        var turn = Assert.Single(plan.Turns);
        Assert.Null(turn.ScreenshotPath);
        Assert.Equal(InferenceModality.Text, turn.Modality);
        Assert.Equal(0, plan.MissingScreenshotFile);
        Assert.Equal(1, plan.WithoutScreenshot);
    }

    [Fact]
    public void Build_CountsSamplesWhoseScreenshotFileIsGone_InsteadOfSilentlyDegrading()
    {
        var plan = GroundingTrainingPlan.Build([Sample(Guid.NewGuid(), "deleted.png")], (_, _) => null);

        var turn = Assert.Single(plan.Turns);
        Assert.Null(turn.ScreenshotPath);
        Assert.Equal(InferenceModality.Text, turn.Modality);
        // The distinguishing signal: recorded WITH a screenshot, trained WITHOUT one.
        Assert.Equal(1, plan.MissingScreenshotFile);
        Assert.Equal(1, plan.WithoutScreenshot);
    }

    [Fact]
    public void Build_WithScreenshotsDisallowed_PlansTextOnly_AndDoesNotCountItAsMissing()
    {
        // The trainer takes this path when the loaded model reports no vision support: a
        // deliberate text-only run is not the same failure as a lost screenshot file.
        var plan = GroundingTrainingPlan.Build(
            [Sample(Guid.NewGuid(), "shot.png")],
            (_, _) => @"C:\uploads\t\u\shot.png",
            allowScreenshots: false);

        Assert.Equal(InferenceModality.Text, Assert.Single(plan.Turns).Modality);
        Assert.Equal(0, plan.MissingScreenshotFile);
        Assert.Equal(1, plan.WithoutScreenshot);
    }

    [Fact]
    public void Build_MixedBatch_ReportsBothCounts()
    {
        var tenantId = Guid.NewGuid();
        var plan = GroundingTrainingPlan.Build(
            [Sample(tenantId, "a.png"), Sample(tenantId), Sample(tenantId, "gone.png")],
            (_, id) => id == "a.png" ? "/tmp/a.png" : null);

        Assert.Equal(3, plan.Turns.Count);
        Assert.Equal(1, plan.WithScreenshot);
        Assert.Equal(2, plan.WithoutScreenshot);
        Assert.Equal(1, plan.MissingScreenshotFile);
    }

    // ── 2. The label and system prompt travel through unchanged ──

    [Fact]
    public void Build_PreservesTheSystemPromptAndTheVettedActionLabel()
    {
        var sample = Sample(Guid.NewGuid());
        var turn = Assert.Single(GroundingTrainingPlan.Build([sample], (_, _) => null).Turns);

        Assert.Equal("SYSTEM", turn.SystemPrompt);
        Assert.Equal("{\"action\":\"click\",\"ref\":1}", turn.AssistantText);
    }

    // ── 3. Training input == inference input ──

    /// <summary>
    /// The training turn must be the turn the live model is actually asked. This pins
    /// <see cref="GroundingTrainingPlan.RenderUserTurn"/> to <c>ComputerUseModel.BuildUserMessage</c>
    /// modulo the ONE field the recorder does not capture: the page <c>title</c>. If either
    /// renderer drifts, or someone adds a captured field to one side only, this fails.
    /// </summary>
    [Fact]
    public void RenderUserTurn_MatchesTheLiveInferencePrompt_ExceptTheUncapturedTitleLine()
    {
        var tenantId = Guid.NewGuid();
        var sample = Sample(tenantId, "shot.png");

        var prompt = new ComputerUsePrompt(
            sample.TaskGoal,
            sample.SystemPrompt,
            new ComputerUseObservation
            {
                Url = sample.PageUrl,
                Title = "Example Domain",
                Elements = [new InteractiveElement(1, "button", "OK", null)],
            },
            History: [],
            ScreenshotPath: "shot.png");

        var inference = ComputerUseModel.BuildUserMessage(prompt, hasScreenshot: true);
        var inferenceWithoutTitle = string.Join('\n',
            inference.Split('\n').Where(line => !line.StartsWith("  title:", StringComparison.Ordinal)));

        Assert.Equal(inferenceWithoutTitle, GroundingTrainingPlan.RenderUserTurn(sample, hasScreenshot: true));

        // Sanity: the shared render really does carry the grounding-critical parts.
        var trainingTurn = GroundingTrainingPlan.RenderUserTurn(sample, hasScreenshot: true);
        Assert.Contains("TASK: Find the pricing page", trainingTurn, StringComparison.Ordinal);
        Assert.Contains("url: https://example.com/", trainingTurn, StringComparison.Ordinal);
        Assert.Contains("[1] button: OK", trainingTurn, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderUserTurn_OnlyClaimsAnAttachedScreenshotWhenThereIsOne()
    {
        var sample = Sample(Guid.NewGuid());

        Assert.Contains("A screenshot of this page is attached.",
            GroundingTrainingPlan.RenderUserTurn(sample, hasScreenshot: true), StringComparison.Ordinal);
        Assert.DoesNotContain("A screenshot of this page is attached.",
            GroundingTrainingPlan.RenderUserTurn(sample, hasScreenshot: false), StringComparison.Ordinal);

        // The instruction itself survives either way.
        Assert.Contains("Respond with EXACTLY ONE action as JSON.",
            GroundingTrainingPlan.RenderUserTurn(sample, hasScreenshot: false), StringComparison.Ordinal);
    }
}

/// <summary>
/// CI tests for <see cref="UploadsGroundingScreenshotLocator"/>: the recorder persists only the
/// stored screenshot NAME (never the owning user), so finding the file again is a tenant-scoped
/// search — and it must stay inside that one tenant's upload root.
/// </summary>
public sealed class GroundingScreenshotLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmkit-shots-{Guid.NewGuid():N}");
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();

    private UploadsGroundingScreenshotLocator Locator() =>
        new(tenantId => Path.Combine(_root, tenantId.ToString("N")));

    private string WriteUpload(Guid tenantId, Guid userId, string fileName)
    {
        var dir = Path.Combine(_root, tenantId.ToString("N"), userId.ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, "png");
        return path;
    }

    [Fact]
    public void Resolve_FindsTheScreenshot_InTheOwningUsersUploadDirectory()
    {
        var expected = WriteUpload(_tenantId, Guid.NewGuid(), "shot.png");
        Assert.Equal(expected, Locator().Resolve(_tenantId, "shot.png"));
    }

    [Fact]
    public void Resolve_SearchesEveryUserDirectoryUnderTheTenant()
    {
        // The recorder does not persist the user id, so the file may sit under any of them.
        WriteUpload(_tenantId, Guid.NewGuid(), "other.png");
        WriteUpload(_tenantId, Guid.NewGuid(), "noise.png");
        var expected = WriteUpload(_tenantId, Guid.NewGuid(), "shot.png");

        Assert.Equal(expected, Locator().Resolve(_tenantId, "shot.png"));
    }

    [Fact]
    public void Resolve_NeverCrossesIntoAnotherTenantsUploads()
    {
        WriteUpload(_otherTenantId, Guid.NewGuid(), "shot.png");
        Directory.CreateDirectory(Path.Combine(_root, _tenantId.ToString("N")));

        Assert.Null(Locator().Resolve(_tenantId, "shot.png"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_ReturnsNull_ForAMissingId(string? id) => Assert.Null(Locator().Resolve(_tenantId, id));

    [Fact]
    public void Resolve_ReturnsNull_WhenTheFileIsGone()
    {
        Directory.CreateDirectory(Path.Combine(_root, _tenantId.ToString("N"), Guid.NewGuid().ToString("N")));
        Assert.Null(Locator().Resolve(_tenantId, "deleted.png"));
    }

    [Fact]
    public void Resolve_TreatsATraversalShapedIdAsABareFileName()
    {
        // A file parked OUTSIDE the tenant root must stay unreachable however the id is spelled.
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "secret.png"), "png");
        Directory.CreateDirectory(Path.Combine(_root, _tenantId.ToString("N"), Guid.NewGuid().ToString("N")));

        Assert.Null(Locator().Resolve(_tenantId, "../secret.png"));
        Assert.Null(Locator().Resolve(_tenantId, "../../secret.png"));
        Assert.Null(Locator().Resolve(_tenantId, Path.Combine(_root, "secret.png")));
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenTheTenantHasNoUploadsAtAll() =>
        Assert.Null(Locator().Resolve(Guid.NewGuid(), "shot.png"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }
}
