using LMKit.Model;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;
using LmKitOmniApi.Infrastructure.AI.Lora;
using LmKitOmniApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// THE regression test for the defect this pipeline shipped with: the grounding LoRA adapter was
/// trained on the CHAT model (<c>LmModelManager.GetChatModelAsync</c>) while every grounding
/// decision is made by the VISION model (<c>ComputerUseModel</c> → <c>GetVisionModelAsync</c>,
/// because the decision needs the screenshot attachment). The adapter was therefore fitted to a
/// model that never makes the decision it is meant to improve.
///
/// <para>
/// No model is loaded and no native code runs: the manager is configured with two REGISTERED
/// model entries whose weight files do not exist, so each role fails fast with a
/// <see cref="FileNotFoundException"/> that NAMES its own file. Which file the exception names is
/// therefore a direct, CI-safe read of which role the trainer asked for.
/// </para>
/// </summary>
public sealed class GroundingAdapterTrainerPortModelRoleTests : IDisposable
{
    private const string ChatWeights = "chat-role-weights.gguf";
    private const string VisionWeights = "vision-role-weights.gguf";

    private readonly string _modelsDirectory = Path.Combine(Path.GetTempPath(), $"lmkit-roles-{Guid.NewGuid():N}");

    private LmModelManager BuildManager()
    {
        // Both roles are registered and both files are ABSENT, so neither role is privileged by
        // accident — only the code's choice decides which failure surfaces.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiModels:ModelsDirectory"] = _modelsDirectory,
                ["AiModels:DefaultChat"] = "chat-role",
                ["AiModels:DefaultVision"] = "vision-role",
                ["AiModels:Models:chat-role:Path"] = ChatWeights,
                ["AiModels:Models:vision-role:Path"] = VisionWeights,
            })
            .Build();

        return new LmModelManager(configuration, NullLogger<LmModelManager>.Instance);
    }

    private static LmKitGroundingAdapterTrainerPort BuildPort(LmModelManager manager) =>
        new(manager,
            new NoScreenshotsLocator(),
            NullLogger<LmKitGroundingAdapterTrainerPort>.Instance);

    private static IReadOnlyList<GroundingSample> OneSample() =>
    [
        new GroundingSample
        {
            TenantId = Guid.NewGuid(),
            TaskGoal = "goal",
            ElementsText = "INTERACTIVE ELEMENTS (address these by 'ref'):\n  [1] button: OK\n",
            SystemPrompt = "sys",
            CorrectActionJson = "{\"action\":\"click\",\"ref\":1}",
        }
    ];

    [Fact]
    public async Task TrainAsync_TrainsTheVisionModel_TheOneThatMakesEveryGroundingDecision()
    {
        using var manager = BuildManager();
        var port = BuildPort(manager);

        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => port.TrainAsync(
            OneSample(),
            new GroundingTrainingOptions(),
            Path.Combine(_modelsDirectory, "adapter.gguf")));

        Assert.Contains(VisionWeights, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ChatWeights, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same fact read from the other side: training must not so much as TOUCH the chat model.
    /// <see cref="LmModelManager.LastChatModelLoadError"/> is set by
    /// <c>GetChatModelAsync</c> on a failed load, so a non-null value here means the chat model
    /// was asked for — which is exactly what the shipped code did.
    /// </summary>
    [Fact]
    public async Task TrainAsync_NeverAsksForTheChatModel()
    {
        using var manager = BuildManager();
        var port = BuildPort(manager);

        await Assert.ThrowsAnyAsync<Exception>(() => port.TrainAsync(
            OneSample(),
            new GroundingTrainingOptions(),
            Path.Combine(_modelsDirectory, "adapter.gguf")));

        Assert.Null(manager.LastChatModelLoadError);
        Assert.False(manager.IsChatModelLoaded);
    }

    /// <summary>
    /// A failed run must not strand the VISION inference lease — that semaphore has one permit by
    /// default (<c>SemaphoreLimits:Vision</c>), so a leak would deadlock every subsequent
    /// computer-use decision rather than just failing this training run.
    /// </summary>
    [Fact]
    public async Task FailedRun_LeavesTheVisionInferenceLeaseAvailable()
    {
        using var manager = BuildManager();
        var port = BuildPort(manager);

        await Assert.ThrowsAnyAsync<Exception>(() => port.TrainAsync(
            OneSample(),
            new GroundingTrainingOptions(),
            Path.Combine(_modelsDirectory, "adapter.gguf")));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var lease = await manager.AcquireVisionInferenceAsync(timeout.Token);
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task TrainAsync_RefusesAnEmptyBatch_BeforeTouchingAnyModel()
    {
        using var manager = BuildManager();
        var port = BuildPort(manager);

        await Assert.ThrowsAsync<InvalidOperationException>(() => port.TrainAsync(
            Array.Empty<GroundingSample>(),
            new GroundingTrainingOptions(),
            Path.Combine(_modelsDirectory, "adapter.gguf")));

        Assert.Null(manager.LastChatModelLoadError);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_modelsDirectory)) Directory.Delete(_modelsDirectory, recursive: true); }
        catch { /* best effort */ }
    }

    private sealed class NoScreenshotsLocator : IGroundingScreenshotLocator
    {
        public string? Resolve(Guid tenantId, string? screenshotFileId) => null;
    }
}

/// <summary>
/// Pins the two halves of the apply path to each other: the name
/// <see cref="GroundingTrainingService"/> registers a trained adapter under, and the filter
/// <c>ComputerUseModel</c> uses to select it again. An adapter that is produced but can never be
/// selected is the same "useless artifact" defect one step further along.
/// </summary>
public sealed class GroundingAdapterNamingTests
{
    private readonly Guid _tenantId = Guid.NewGuid();

    [Fact]
    public void NewName_IsSelectableByTheSelectorsOwnFilter()
    {
        var name = GroundingAdapterNaming.NewName(new DateTime(2026, 9, 7, 10, 30, 0, DateTimeKind.Utc));

        Assert.StartsWith(GroundingAdapterNaming.Prefix, name, StringComparison.Ordinal);
        Assert.Contains("20260907103000", name, StringComparison.Ordinal);
        Assert.True(GroundingAdapterNaming.IsGroundingAdapter(name));
    }

    [Fact]
    public void NewName_IsUniquePerCall()
    {
        var utc = DateTime.UtcNow;
        var names = Enumerable.Range(0, 50).Select(_ => GroundingAdapterNaming.NewName(utc)).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("my-adapter")]
    [InlineData("Grounding-2026")]      // case-sensitive on purpose: an operator's adapter is theirs
    [InlineData("legal-grounding-v2")]
    public void IsGroundingAdapter_RejectsAdaptersTheTrainerDidNotProduce(string? name) =>
        Assert.False(GroundingAdapterNaming.IsGroundingAdapter(name));

    [Fact]
    public async Task SelectNewestActive_PicksTheNewestActiveAutoTrainedAdapter()
    {
        var newest = Registration(GroundingAdapterNaming.NewName(DateTime.UtcNow), isActive: true);
        var older = Registration(GroundingAdapterNaming.NewName(DateTime.UtcNow.AddDays(-1)), isActive: true);
        var lora = new ListOnlyLoraService(enabled: true, [newest, older]);

        Assert.Equal(newest.Id, await GroundingAdapterNaming.SelectNewestActiveAsync(lora, _tenantId));
    }

    [Fact]
    public async Task SelectNewestActive_SkipsDeactivatedAdapters_FallingBackToTheOneBefore()
    {
        var deactivated = Registration(GroundingAdapterNaming.NewName(DateTime.UtcNow), isActive: false);
        var previous = Registration(GroundingAdapterNaming.NewName(DateTime.UtcNow.AddDays(-1)), isActive: true);
        var lora = new ListOnlyLoraService(enabled: true, [deactivated, previous]);

        Assert.Equal(previous.Id, await GroundingAdapterNaming.SelectNewestActiveAsync(lora, _tenantId));
    }

    [Fact]
    public async Task SelectNewestActive_IgnoresAdaptersTheGroundingTrainerDidNotProduce()
    {
        var lora = new ListOnlyLoraService(enabled: true,
        [
            Registration("hand-uploaded-adapter", isActive: true),
            Registration("summarizer", isActive: true),
        ]);

        Assert.Null(await GroundingAdapterNaming.SelectNewestActiveAsync(lora, _tenantId));
    }

    [Fact]
    public async Task SelectNewestActive_ReturnsNull_WhenTheLoraFeatureIsOff()
    {
        var lora = new ListOnlyLoraService(enabled: false,
            [Registration(GroundingAdapterNaming.NewName(DateTime.UtcNow), isActive: true)]);

        Assert.Null(await GroundingAdapterNaming.SelectNewestActiveAsync(lora, _tenantId));
        Assert.Equal(0, lora.ListCalls); // and it never even asks
    }

    private LoraAdapterRegistration Registration(string name, bool isActive) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = _tenantId,
        Name = name,
        FilePath = "adapter.gguf",
        IsActive = isActive,
    };

    /// <summary>Only <c>Enabled</c> + <c>ListAsync</c> participate in selection; everything else must not be reached.</summary>
    private sealed class ListOnlyLoraService : ILoraAdapterService
    {
        private readonly IReadOnlyList<LoraAdapterRegistration> _registrations;

        public ListOnlyLoraService(bool enabled, IReadOnlyList<LoraAdapterRegistration> registrations)
        {
            Enabled = enabled;
            _registrations = registrations;
        }

        public bool Enabled { get; }
        public int ListCalls { get; private set; }

        public Task<IReadOnlyList<LoraAdapterRegistration>> ListAsync(Guid tenantId, CancellationToken ct = default)
        {
            ListCalls++;
            return Task.FromResult(_registrations);
        }

        public Task<LoraAdapterRegistration> RegisterAsync(Guid tenantId, string name, string? description, Stream content, long contentLength, float? scale, string? targetModelId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LoraAdapterRegistration?> GetAsync(Guid tenantId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LoraAdapterRegistration?> SetActiveAsync(Guid tenantId, Guid id, bool isActive, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LoraAdapterRegistration?> UpdateAsync(Guid tenantId, Guid id, string? name, float? scale, bool? isActive, CancellationToken ct = default) => throw new NotSupportedException();
        public LoraApplyScope? BeginApplyForAgent(LM model, Guid tenantId, Guid? loraAdapterId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
