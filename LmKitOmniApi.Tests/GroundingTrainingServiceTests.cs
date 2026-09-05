using LMKit.Model;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;
using LmKitOmniApi.Infrastructure.AI.Lora;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// CI tests for <see cref="GroundingTrainingService"/> orchestration with a FAKE
/// <see cref="IGroundingAdapterTrainerPort"/> (no model, no compute) and a fake
/// <see cref="ILoraAdapterService"/>: it refuses when disabled, refuses below the minimum
/// sample count, and on success calls the trainer port THEN registers the produced adapter
/// (order asserted via a shared call log).
/// </summary>
public sealed class GroundingTrainingServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmkit-gts-{Guid.NewGuid():N}");
    private readonly Guid _tenantId = Guid.NewGuid();

    private GroundingTrainingService Create(
        FakeTrainerPort port,
        FakeLoraAdapterService lora,
        int sampleCount,
        bool enabled = true,
        int minSamples = 1)
    {
        var options = new GroundingTrainingOptions
        {
            Enabled = enabled,
            MinSamplesToTrain = minSamples,
            AdapterOutputPath = _root,
            Rank = 8,
            Alpha = 16,
            Epochs = 1,
            LearningRate = 1e-4f,
        };
        return new GroundingTrainingService(
            Options.Create(options),
            new FakeRecorder(sampleCount),
            port,
            lora,
            NullLogger<GroundingTrainingService>.Instance);
    }

    [Fact]
    public async Task Disabled_TrainAsync_Refuses_AndNeverTrainsOrRegisters()
    {
        var log = new List<string>();
        var port = new FakeTrainerPort(log);
        var lora = new FakeLoraAdapterService(log);
        var service = Create(port, lora, sampleCount: 100, enabled: false, minSamples: 1);

        Assert.False(service.Enabled);
        var result = await service.TrainAsync(_tenantId);

        Assert.Equal(GroundingTrainingStatus.Disabled, result.Status);
        Assert.Equal(0, port.TrainCount);
        Assert.Equal(0, lora.RegisterCount);
        Assert.Empty(log);
    }

    [Fact]
    public async Task BelowMinSamples_TrainAsync_Refuses_AndNeverTrainsOrRegisters()
    {
        var log = new List<string>();
        var port = new FakeTrainerPort(log);
        var lora = new FakeLoraAdapterService(log);
        var service = Create(port, lora, sampleCount: 3, enabled: true, minSamples: 50);

        var result = await service.TrainAsync(_tenantId);

        Assert.Equal(GroundingTrainingStatus.InsufficientSamples, result.Status);
        Assert.Equal(3, result.SampleCount);
        Assert.Equal(50, result.RequiredSamples);
        Assert.Equal(0, port.TrainCount);
        Assert.Equal(0, lora.RegisterCount);
        Assert.Empty(log);
    }

    [Fact]
    public async Task Success_CallsPort_ThenRegistersProducedAdapter_InOrder()
    {
        var log = new List<string>();
        var port = new FakeTrainerPort(log);
        var lora = new FakeLoraAdapterService(log);
        var service = Create(port, lora, sampleCount: 5, enabled: true, minSamples: 1);

        var result = await service.TrainAsync(_tenantId);

        Assert.Equal(GroundingTrainingStatus.Trained, result.Status);
        Assert.Equal(lora.RegisteredId, result.AdapterId);
        Assert.Equal(5, result.SampleCount);
        Assert.EndsWith(".gguf", result.AdapterPath);

        Assert.Equal(1, port.TrainCount);
        Assert.Equal(5, port.LastSamples!.Count);
        Assert.Equal(1, lora.RegisterCount);

        // The port must be called BEFORE registration (the adapter has to exist first).
        Assert.Equal(new[] { "train", "register" }, log);
        // And the exact produced adapter file was the thing registered.
        Assert.Equal(FakeTrainerPort.AdapterBytes, lora.RegisteredContent);
        Assert.Equal(_tenantId, lora.RegisteredTenantId);
    }

    [Fact]
    public async Task Success_ButLoraFeatureOff_ReturnsTrainedNotRegistered()
    {
        var log = new List<string>();
        var port = new FakeTrainerPort(log);
        var lora = new FakeLoraAdapterService(log) { ThrowFeatureDisabled = true };
        var service = Create(port, lora, sampleCount: 5, enabled: true, minSamples: 1);

        var result = await service.TrainAsync(_tenantId);

        Assert.Equal(GroundingTrainingStatus.TrainedNotRegistered, result.Status);
        Assert.Null(result.AdapterId);
        Assert.Equal(5, result.SampleCount);
        Assert.Equal(1, port.TrainCount); // trained
        Assert.Equal(new[] { "train", "register" }, log); // register was attempted
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    // ── Fakes ──

    internal sealed class FakeTrainerPort : IGroundingAdapterTrainerPort
    {
        public const string AdapterBytes = "FAKE-GROUNDING-ADAPTER";

        private readonly List<string> _log;
        public FakeTrainerPort(List<string> log) => _log = log;

        public int TrainCount { get; private set; }
        public IReadOnlyList<GroundingSample>? LastSamples { get; private set; }
        public GroundingTrainingOptions? LastOptions { get; private set; }

        public Task<GroundingTrainResult> TrainAsync(
            IReadOnlyList<GroundingSample> samples, GroundingTrainingOptions opts, string adapterOutputPath, CancellationToken ct = default)
        {
            TrainCount++;
            LastSamples = samples;
            LastOptions = opts;
            _log.Add("train");
            // Emit a real file so the service can open + register it (proving order + content).
            Directory.CreateDirectory(Path.GetDirectoryName(adapterOutputPath)!);
            File.WriteAllText(adapterOutputPath, AdapterBytes);
            return Task.FromResult(new GroundingTrainResult(adapterOutputPath, samples.Count));
        }
    }

    private sealed class FakeRecorder : IGroundingTraceRecorder
    {
        private readonly IReadOnlyList<GroundingSample> _samples;
        public FakeRecorder(int count) => _samples = Enumerable.Range(0, count)
            .Select(i => new GroundingSample
            {
                TaskGoal = "goal-" + i,
                ElementsText = "els",
                SystemPrompt = "sys",
                CorrectActionJson = "{\"action\":\"click\",\"ref\":1}",
            })
            .ToList();

        public bool Enabled => true;
        public Task RecordAsync(GroundingSample sample, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<GroundingSample>> ReadAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult(_samples);
        public Task<int> CountAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult(_samples.Count);
    }

    internal sealed class FakeLoraAdapterService : ILoraAdapterService
    {
        private readonly List<string> _log;
        public FakeLoraAdapterService(List<string> log) => _log = log;

        public bool Enabled => true;
        public bool ThrowFeatureDisabled { get; init; }
        public int RegisterCount { get; private set; }
        public string? RegisteredContent { get; private set; }
        public Guid RegisteredTenantId { get; private set; }
        public Guid RegisteredId { get; } = Guid.NewGuid();

        public async Task<LoraAdapterRegistration> RegisterAsync(
            Guid tenantId, string name, string? description, Stream content, long contentLength,
            float? scale, string? targetModelId, CancellationToken ct = default)
        {
            RegisterCount++;
            _log.Add("register");
            if (ThrowFeatureDisabled) throw new LoraFeatureDisabledException();

            using var reader = new StreamReader(content);
            RegisteredContent = await reader.ReadToEndAsync(ct);
            RegisteredTenantId = tenantId;
            return new LoraAdapterRegistration { Id = RegisteredId, TenantId = tenantId, Name = name, FileSizeBytes = contentLength };
        }

        public Task<IReadOnlyList<LoraAdapterRegistration>> ListAsync(Guid tenantId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LoraAdapterRegistration?> GetAsync(Guid tenantId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LoraAdapterRegistration?> SetActiveAsync(Guid tenantId, Guid id, bool isActive, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LoraAdapterRegistration?> UpdateAsync(Guid tenantId, Guid id, string? name, float? scale, bool? isActive, CancellationToken ct = default) => throw new NotSupportedException();
        public LoraApplyScope? BeginApplyForAgent(LM model, Guid tenantId, Guid? loraAdapterId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

/// <summary>
/// Integration proof that a produced grounding adapter actually becomes a hot-swappable LoRA
/// registration: the REAL <see cref="LoraAdapterService"/> (in-memory SQLite + a fake
/// <see cref="ILoraModelPort"/>, LoRA feature ON) registers the adapter file emitted by the
/// fake trainer port, so a row appears and its file lands under the tenant-scoped store.
/// </summary>
[Collection("DbSqlite")]
public sealed class GroundingTrainingServiceRegistrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly HermesDbContext _db;
    private readonly string _loraDir = Path.Combine(Path.GetTempPath(), $"lmkit-gts-lora-{Guid.NewGuid():N}");
    private readonly string _outputDir = Path.Combine(Path.GetTempPath(), $"lmkit-gts-out-{Guid.NewGuid():N}");
    private readonly Guid _tenantId = Guid.NewGuid();

    public GroundingTrainingServiceRegistrationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new HermesDbContext(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Test tenant" });
        _db.SaveChanges();
    }

    [Fact]
    public async Task TrainAsync_RegistersProducedAdapter_AsHotSwappableRegistration()
    {
        var loraService = new LoraAdapterService(
            _db,
            new FakeLoraModelPort(),                  // stands in for format-validate + apply/remove
            Options.Create(new LoraOptions { Enabled = true, AdapterStoragePath = _loraDir, MaxAdapterBytes = 1_000_000, MaxScale = 2.0f }),
            NullLogger<LoraAdapterService>.Instance);

        var options = new GroundingTrainingOptions { Enabled = true, MinSamplesToTrain = 1, AdapterOutputPath = _outputDir };
        var service = new GroundingTrainingService(
            Options.Create(options),
            new SeededRecorder(2),
            new GroundingTrainingServiceTests.FakeTrainerPort(new List<string>()),
            loraService,
            NullLogger<GroundingTrainingService>.Instance);

        var result = await service.TrainAsync(_tenantId);

        Assert.Equal(GroundingTrainingStatus.Trained, result.Status);
        Assert.NotNull(result.AdapterId);

        // The produced adapter is now a real, tenant-scoped LoRA registration (hot-swappable).
        var registrations = await loraService.ListAsync(_tenantId);
        var reg = Assert.Single(registrations);
        Assert.Equal(result.AdapterId, reg.Id);
        Assert.True(reg.IsActive);
        Assert.StartsWith(Path.Combine(_loraDir, _tenantId.ToString("N")), reg.FilePath);
        Assert.True(File.Exists(reg.FilePath));
    }

    private sealed class SeededRecorder : IGroundingTraceRecorder
    {
        private readonly IReadOnlyList<GroundingSample> _samples;
        public SeededRecorder(int count) => _samples = Enumerable.Range(0, count)
            .Select(i => new GroundingSample { TaskGoal = "g" + i, SystemPrompt = "sys", CorrectActionJson = "{}" })
            .ToList();
        public bool Enabled => true;
        public Task RecordAsync(GroundingSample sample, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<GroundingSample>> ReadAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult(_samples);
        public Task<int> CountAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult(_samples.Count);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { if (Directory.Exists(_loraDir)) Directory.Delete(_loraDir, recursive: true); } catch { /* best effort */ }
        try { if (Directory.Exists(_outputDir)) Directory.Delete(_outputDir, recursive: true); } catch { /* best effort */ }
    }
}
