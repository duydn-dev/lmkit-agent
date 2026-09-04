using System.Text;
using LMKit.Model;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.Lora;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// LoRA hot-swap service, proven end-to-end in CI with an in-memory SQLite context and a
/// fake <see cref="ILoraModelPort"/> (so no native model and no real adapter file are
/// needed — the fake stands in for both the format validation and the apply/remove).
/// Covers the off-by-default gate, the size cap, format rejection, the register/list/delete
/// roundtrip, and the BeginApplyForAgent no-op conditions.
/// </summary>
[Collection("DbSqlite")]
public sealed class LoraAdapterServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly HermesDbContext _db;
    private readonly string _storageDir;
    private readonly Guid _tenantId = Guid.NewGuid();

    public LoraAdapterServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new HermesDbContext(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Test tenant" });
        _db.SaveChanges();

        _storageDir = Path.Combine(Path.GetTempPath(), $"lmkit-lora-{Guid.NewGuid():N}");
    }

    private LoraAdapterService CreateService(FakeLoraModelPort port, bool enabled = true, long maxBytes = 1_000_000)
    {
        var options = new LoraOptions
        {
            Enabled = enabled,
            AdapterStoragePath = _storageDir,
            MaxAdapterBytes = maxBytes,
            DefaultScale = 1.0f,
            MinScale = 0f,
            MaxScale = 2.0f
        };
        return new LoraAdapterService(_db, port, Options.Create(options), NullLogger<LoraAdapterService>.Instance);
    }

    private static MemoryStream Bytes(int count) => new(Encoding.ASCII.GetBytes(new string('a', count)));

    private string TenantDir => Path.Combine(_storageDir, _tenantId.ToString("N"));

    // ── 1. Off-by-default gate ──

    [Fact]
    public async Task Disabled_RegisterAsync_Throws_FeatureDisabled()
    {
        var service = CreateService(new FakeLoraModelPort(), enabled: false);
        await Assert.ThrowsAsync<LoraFeatureDisabledException>(() =>
            service.RegisterAsync(_tenantId, "a", null, Bytes(10), 10, null, null, CancellationToken.None));
    }

    [Fact]
    public void Disabled_BeginApplyForAgent_ReturnsNull()
    {
        var service = CreateService(new FakeLoraModelPort(), enabled: false);
        var scope = service.BeginApplyForAgent(model: null!, _tenantId, Guid.NewGuid(), CancellationToken.None);
        Assert.Null(scope);
    }

    [Fact]
    public void IsEnabled_ReflectsOptions()
    {
        Assert.True(CreateService(new FakeLoraModelPort(), enabled: true).Enabled);
        Assert.False(CreateService(new FakeLoraModelPort(), enabled: false).Enabled);
    }

    // ── 2. Size cap ──

    [Fact]
    public async Task RegisterAsync_OverMaxAdapterBytes_Throws_AndLeavesNoRowOrFile()
    {
        var service = CreateService(new FakeLoraModelPort(), maxBytes: 16);

        await Assert.ThrowsAsync<LoraAdapterValidationException>(() =>
            service.RegisterAsync(_tenantId, "too-big", null, Bytes(64), 64, null, null, CancellationToken.None));

        Assert.Equal(0, await _db.LoraAdapterRegistrations.CountAsync());
        Assert.True(!Directory.Exists(TenantDir) || Directory.GetFiles(TenantDir).Length == 0,
            "A rejected upload must leave no adapter file behind.");
    }

    [Fact]
    public async Task RegisterAsync_DeclaredLengthOverCap_Throws_BeforeStreaming()
    {
        var service = CreateService(new FakeLoraModelPort(), maxBytes: 16);
        await Assert.ThrowsAsync<LoraAdapterValidationException>(() =>
            service.RegisterAsync(_tenantId, "big", null, Bytes(8), contentLength: 999, null, null, CancellationToken.None));
        Assert.Equal(0, await _db.LoraAdapterRegistrations.CountAsync());
    }

    // ── 3. Format validation seam ──

    [Fact]
    public async Task RegisterAsync_InvalidFormat_Throws_AndLeavesNoRowOrFile()
    {
        var service = CreateService(new FakeLoraModelPort { ValidateResult = false });

        await Assert.ThrowsAsync<LoraAdapterValidationException>(() =>
            service.RegisterAsync(_tenantId, "bad", null, Bytes(32), 32, null, null, CancellationToken.None));

        Assert.Equal(0, await _db.LoraAdapterRegistrations.CountAsync());
        Assert.True(!Directory.Exists(TenantDir) || Directory.GetFiles(TenantDir).Length == 0);
    }

    // ── 4. Register + list + get + delete roundtrip ──

    [Fact]
    public async Task Register_List_Get_Delete_Roundtrip()
    {
        var port = new FakeLoraModelPort();
        var service = CreateService(port);

        var registration = await service.RegisterAsync(
            _tenantId, "My Adapter", "desc", Bytes(50), 50, scale: 1.5f, targetModelId: "qwen3.5:2b", CancellationToken.None);

        // Persisted with a server-generated, tenant-scoped path — never the upload name.
        Assert.Equal(50, registration.FileSizeBytes);
        Assert.Equal(1.5f, registration.Scale);
        Assert.True(registration.IsActive);
        Assert.StartsWith(TenantDir, registration.FilePath);
        Assert.True(File.Exists(registration.FilePath));

        var list = await service.ListAsync(_tenantId);
        Assert.Single(list);
        Assert.Equal(registration.Id, list[0].Id);

        var fetched = await service.GetAsync(_tenantId, registration.Id);
        Assert.NotNull(fetched);

        // Cross-tenant read sees nothing.
        Assert.Null(await service.GetAsync(Guid.NewGuid(), registration.Id));

        var deleted = await service.DeleteAsync(_tenantId, registration.Id);
        Assert.True(deleted);
        Assert.False(File.Exists(registration.FilePath));
        Assert.Empty(await service.ListAsync(_tenantId));
    }

    [Fact]
    public async Task RegisterAsync_DuplicateName_Throws()
    {
        var service = CreateService(new FakeLoraModelPort());
        await service.RegisterAsync(_tenantId, "dup", null, Bytes(10), 10, null, null, CancellationToken.None);
        await Assert.ThrowsAsync<LoraAdapterValidationException>(() =>
            service.RegisterAsync(_tenantId, "dup", null, Bytes(10), 10, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task RegisterAsync_ClampsScale_ToConfiguredBounds()
    {
        var service = CreateService(new FakeLoraModelPort()); // MaxScale = 2.0
        var reg = await service.RegisterAsync(_tenantId, "clamp", null, Bytes(10), 10, scale: 9.0f, null, CancellationToken.None);
        Assert.Equal(2.0f, reg.Scale);
    }

    // ── 5. SetActive / Update ──

    [Fact]
    public async Task SetActiveAsync_And_UpdateAsync_MutateInPlace()
    {
        var service = CreateService(new FakeLoraModelPort());
        var reg = await service.RegisterAsync(_tenantId, "up", null, Bytes(10), 10, null, null, CancellationToken.None);

        var deactivated = await service.SetActiveAsync(_tenantId, reg.Id, false);
        Assert.NotNull(deactivated);
        Assert.False(deactivated!.IsActive);

        var updated = await service.UpdateAsync(_tenantId, reg.Id, name: "renamed", scale: 0.25f, isActive: true);
        Assert.NotNull(updated);
        Assert.Equal("renamed", updated!.Name);
        Assert.Equal(0.25f, updated.Scale);
        Assert.True(updated.IsActive);

        Assert.Null(await service.UpdateAsync(_tenantId, Guid.NewGuid(), "x", null, null));
    }

    // ── 6. BeginApplyForAgent no-op conditions + apply/remove ──

    [Fact]
    public async Task BeginApplyForAgent_ReturnsNull_WhenMissingOrInactiveOrFileGone()
    {
        var service = CreateService(new FakeLoraModelPort());

        // (a) unknown id
        Assert.Null(service.BeginApplyForAgent(null!, _tenantId, Guid.NewGuid(), CancellationToken.None));

        // (b) inactive
        var inactive = await service.RegisterAsync(_tenantId, "inactive", null, Bytes(10), 10, null, null, CancellationToken.None);
        await service.SetActiveAsync(_tenantId, inactive.Id, false);
        Assert.Null(service.BeginApplyForAgent(null!, _tenantId, inactive.Id, CancellationToken.None));

        // (c) active but file removed on disk
        var gone = await service.RegisterAsync(_tenantId, "gone", null, Bytes(10), 10, null, null, CancellationToken.None);
        File.Delete(gone.FilePath);
        Assert.Null(service.BeginApplyForAgent(null!, _tenantId, gone.Id, CancellationToken.None));
    }

    [Fact]
    public async Task BeginApplyForAgent_AppliesAndRemoves_WhenActiveWithFilePresent()
    {
        var port = new FakeLoraModelPort();
        var service = CreateService(port);
        var reg = await service.RegisterAsync(_tenantId, "live", null, Bytes(10), 10, scale: 0.75f, null, CancellationToken.None);

        var scope = service.BeginApplyForAgent(null!, _tenantId, reg.Id, CancellationToken.None);

        Assert.NotNull(scope);
        Assert.True(port.Applied);
        Assert.Equal(reg.FilePath, port.LastPath);
        Assert.Equal(0.75f, port.LastScale);
        Assert.False(port.Removed);

        scope!.Dispose();
        Assert.True(port.Removed);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { if (Directory.Exists(_storageDir)) Directory.Delete(_storageDir, recursive: true); }
        catch { /* best effort */ }
    }
}

/// <summary>
/// Fake <see cref="ILoraModelPort"/> for CI: stands in for both the LM-Kit format
/// validation and the apply/remove on a native model, and records the apply/remove
/// sequence so ordering (and removal-on-throw) can be asserted without a real model.
/// </summary>
internal sealed class FakeLoraModelPort : ILoraModelPort
{
    private int _seq;

    public bool ValidateResult { get; set; } = true;
    public int ApplyCount { get; private set; }
    public string? LastPath { get; private set; }
    public float LastScale { get; private set; }
    public bool Applied { get; private set; }
    public bool Removed { get; private set; }
    public int AppliedSeq { get; private set; } = -1;
    public int RemovedSeq { get; private set; } = -1;

    public IDisposable Apply(LM model, string adapterPath, float scale)
    {
        ApplyCount++;
        LastPath = adapterPath;
        LastScale = scale;
        Applied = true;
        AppliedSeq = ++_seq;
        return new Handle(this);
    }

    public IReadOnlyList<string> ListApplied(LM model) =>
        Applied && !Removed && LastPath is not null ? new[] { LastPath } : Array.Empty<string>();

    public bool ValidateFormat(string adapterPath) => ValidateResult;

    private sealed class Handle : IDisposable
    {
        private readonly FakeLoraModelPort _port;
        private bool _disposed;
        public Handle(FakeLoraModelPort port) => _port = port;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _port.Removed = true;
            _port.RemovedSeq = ++_port._seq;
        }
    }
}
