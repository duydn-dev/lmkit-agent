using System.Text;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.Lora;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The per-request apply scope returned by <see cref="ILoraAdapterService.BeginApplyForAgent"/>.
/// Uses the shared <see cref="FakeLoraModelPort"/> to prove the two guarantees the chat
/// wiring depends on: the adapter is applied BEFORE the scope is disposed, and it is
/// removed EXACTLY when the scope is disposed — including when the wrapped body throws
/// (mirroring <c>using var loraScope = ...;</c> around inference that faults).
/// </summary>
[Collection("DbSqlite")]
public sealed class LoraApplyScopeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly HermesDbContext _db;
    private readonly string _storageDir;
    private readonly Guid _tenantId = Guid.NewGuid();

    public LoraApplyScopeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new HermesDbContext(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Test tenant" });
        _db.SaveChanges();
        _storageDir = Path.Combine(Path.GetTempPath(), $"lmkit-lora-scope-{Guid.NewGuid():N}");
    }

    private (LoraAdapterService service, LoraAdapterRegistration reg) CreateWithActiveAdapter(FakeLoraModelPort port)
    {
        var service = new LoraAdapterService(
            _db, port,
            Options.Create(new LoraOptions
            {
                Enabled = true,
                AdapterStoragePath = _storageDir,
                MinScale = 0f,
                MaxScale = 2.0f,
                DefaultScale = 1.0f
            }),
            NullLogger<LoraAdapterService>.Instance);

        var content = new MemoryStream(Encoding.ASCII.GetBytes("adapter-bytes"));
        var reg = service.RegisterAsync(_tenantId, "scoped", null, content, content.Length, null, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        return (service, reg);
    }

    [Fact]
    public void Dispose_RemovesAdapter_AfterApply_InOrder()
    {
        var port = new FakeLoraModelPort();
        var (service, reg) = CreateWithActiveAdapter(port);

        var scope = service.BeginApplyForAgent(null!, _tenantId, reg.Id, CancellationToken.None);
        Assert.NotNull(scope);

        // Applied, not yet removed.
        Assert.True(port.Applied);
        Assert.False(port.Removed);

        scope!.Dispose();

        Assert.True(port.Removed);
        Assert.True(port.AppliedSeq > 0 && port.RemovedSeq > port.AppliedSeq,
            "Remove must happen strictly after Apply.");
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var port = new FakeLoraModelPort();
        var (service, reg) = CreateWithActiveAdapter(port);

        var scope = service.BeginApplyForAgent(null!, _tenantId, reg.Id, CancellationToken.None)!;
        scope.Dispose();
        var removedSeqAfterFirst = port.RemovedSeq;
        scope.Dispose(); // second dispose must be a no-op

        Assert.Equal(removedSeqAfterFirst, port.RemovedSeq);
    }

    [Fact]
    public void Adapter_IsRemoved_EvenWhenWrappedBodyThrows()
    {
        var port = new FakeLoraModelPort();
        var (service, reg) = CreateWithActiveAdapter(port);

        var scope = service.BeginApplyForAgent(null!, _tenantId, reg.Id, CancellationToken.None);
        Assert.NotNull(scope);
        Assert.True(port.Applied);

        // Mirrors the orchestrator's `using var loraScope = BeginApplyForAgent(...);`
        // around inference that faults — the removal MUST still run.
        InvalidOperationException? thrown = null;
        try
        {
            using (scope)
            {
                throw new InvalidOperationException("inference boom");
            }
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        Assert.NotNull(thrown);
        Assert.Equal("inference boom", thrown!.Message);
        Assert.True(port.Removed, "The adapter must be removed even when the wrapped body throws.");
        Assert.True(port.RemovedSeq > port.AppliedSeq);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { if (Directory.Exists(_storageDir)) Directory.Delete(_storageDir, recursive: true); }
        catch { /* best effort */ }
    }
}
