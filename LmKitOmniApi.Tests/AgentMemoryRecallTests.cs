using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Recall and memory-context contracts for <see cref="AgentMemoryService"/>.
///
/// Two behaviours are pinned:
/// <list type="number">
/// <item><c>GetMemoryContextAsync</c> returns an EMPTY string when nothing was recalled.
/// The wrapper used to be emitted unconditionally, so
/// <c>AgentOrchestrator</c>'s <c>!string.IsNullOrEmpty(memoryContext)</c> guard was always
/// true and the UI claimed "🧠 Đã tìm thấy ký ức liên quan" for every turn, with zero
/// memories — and the model got an empty "Recalled Facts" header every time.</item>
/// <item>The confirmation gate. <c>ExtractAndStoreFactsAsync</c> writes inferred facts with
/// <c>IsConfirmed = false</c> by design, and recall filters them out by DEFAULT, so the
/// extract → recall loop only closes once a human confirms
/// (<c>POST /api/memory/{id}/confirm</c>). <c>AgentMemory:RecallUnconfirmed = true</c>
/// opts a deployment into recalling them automatically; scope and expiry are unaffected
/// either way.</item>
/// </list>
///
/// Hermetic: SQLite in memory, a no-op distributed cache (every call takes the DB path),
/// a stub vector store, and an <see cref="LmModelManager"/> configured with a
/// non-HTTPS embedding-model id so <c>GetEmbeddingModelAsync</c> fails IMMEDIATELY on the
/// scheme check — no DNS, no download. That drops recall onto its documented
/// keyword/recency-only scoring path, which is what these assertions exercise.
/// </summary>
[Collection("DbSqlite")]
public sealed class AgentMemoryRecallTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly HermesDbContext _db;
    private readonly LmModelManager _modelManager;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    public AgentMemoryRecallTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new HermesDbContext(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Tenants.Add(new Tenant { Id = _tenantId, Name = "Memory tenant" });
        _db.Users.Add(NewUser(_userId));
        _db.SaveChanges();

        _modelManager = new LmModelManager(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // http:// (not https) is rejected by the model manager's SSRF pre-flight
                // before any network work happens — a deterministic, offline failure.
                ["AiModels:DefaultEmbedding"] = "http://127.0.0.1:9/no-such-model.gguf"
            })
            .Build());
    }

    private User NewUser(Guid id) => new()
    {
        Id = id,
        TenantId = _tenantId,
        Username = $"u{id:N}",
        Email = $"{id:N}@example.test",
        PasswordHash = "x",
        FullName = "Test user"
    };

    private AgentMemoryService CreateService(bool recallUnconfirmed) =>
        new(_db,
            _modelManager,
            new StubVectorStore(),
            NullLogger<AgentMemoryService>.Instance,
            new NoOpDistributedCache(),
            Options.Create(new AgentMemoryOptions { RecallUnconfirmed = recallUnconfirmed }));

    private AgentMemory AddMemory(bool isConfirmed, string key = "user_name", string value = "Lan Anh")
    {
        var memory = new AgentMemory
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            UserId = _userId,
            MemoryType = "UserProfile",
            MemoryKey = key,
            MemoryValue = value,
            Confidence = 0.7f,
            IsConfirmed = isConfirmed,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        _db.AgentMemories.Add(memory);
        _db.SaveChanges();
        return memory;
    }

    // ── 1. The "found memories" guard must be able to be false ──

    [Fact]
    public async Task GetMemoryContext_IsEmpty_WhenNothingWasRecalled()
    {
        var service = CreateService(recallUnconfirmed: false);

        var context = await service.GetMemoryContextAsync(_tenantId, _userId, "thủ đô nào", CancellationToken.None);

        // The orchestrator's status marker keys off exactly this emptiness check.
        Assert.Equal(string.Empty, context);
        Assert.True(string.IsNullOrEmpty(context));
        Assert.DoesNotContain("Agent Memory", context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMemoryContext_IsEmpty_WhenOnlyUnconfirmedMemoriesExist_AndRecallIsDefault()
    {
        AddMemory(isConfirmed: false);
        var service = CreateService(recallUnconfirmed: false);

        var context = await service.GetMemoryContextAsync(_tenantId, _userId, "user_name", CancellationToken.None);

        Assert.Equal(string.Empty, context);
    }

    [Fact]
    public async Task GetMemoryContext_RendersTheBlock_WhenSomethingWasRecalled()
    {
        AddMemory(isConfirmed: true);
        var service = CreateService(recallUnconfirmed: false);

        var context = await service.GetMemoryContextAsync(_tenantId, _userId, "user_name", CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(context));
        Assert.Contains("--- Agent Memory (Recalled Facts) ---", context, StringComparison.Ordinal);
        Assert.Contains("Lan Anh", context, StringComparison.Ordinal);
        Assert.Contains("--- End Memory ---", context, StringComparison.Ordinal);
    }

    // ── 2. The confirmation gate, both settings ──

    [Fact]
    public async Task Recall_ExcludesUnconfirmedMemories_ByDefault()
    {
        AddMemory(isConfirmed: false, key: "user_name", value: "Lan Anh");
        AddMemory(isConfirmed: true, key: "preferred_language", value: "tiếng Việt");
        var service = CreateService(recallUnconfirmed: false);

        var recalled = await service.RecallMemoriesAsync(_tenantId, _userId, "user_name preferred_language", ct: CancellationToken.None);

        Assert.Single(recalled);
        Assert.Equal("preferred_language", recalled[0].Key);
    }

    [Fact]
    public async Task Recall_IncludesUnconfirmedMemories_WhenRecallUnconfirmedIsEnabled()
    {
        AddMemory(isConfirmed: false, key: "user_name", value: "Lan Anh");
        AddMemory(isConfirmed: true, key: "preferred_language", value: "tiếng Việt");
        var service = CreateService(recallUnconfirmed: true);

        var recalled = await service.RecallMemoriesAsync(_tenantId, _userId, "user_name preferred_language", ct: CancellationToken.None);

        Assert.Equal(2, recalled.Count);
        Assert.Contains(recalled, r => r.Key == "user_name");
        Assert.Contains(recalled, r => r.Key == "preferred_language");
    }

    [Fact]
    public async Task RecallUnconfirmed_DoesNotWidenMemoryScope()
    {
        // Another user's private memory stays invisible even with the switch on: the
        // option relaxes the CONFIRMATION requirement only, never MemoryScopePolicy.
        var otherUser = NewUser(Guid.NewGuid());
        _db.Users.Add(otherUser);
        _db.SaveChanges();

        var foreign = new AgentMemory
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            UserId = otherUser.Id,
            MemoryType = "UserProfile",
            MemoryKey = "user_name",
            MemoryValue = "Người khác",
            Confidence = 0.7f,
            IsConfirmed = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        _db.AgentMemories.Add(foreign);
        _db.SaveChanges();

        var service = CreateService(recallUnconfirmed: true);

        var recalled = await service.RecallMemoriesAsync(_tenantId, _userId, "user_name", ct: CancellationToken.None);

        Assert.Empty(recalled);
    }

    [Fact]
    public async Task RecallUnconfirmed_StillHonoursExpiry()
    {
        var expired = new AgentMemory
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            UserId = _userId,
            MemoryType = "UserProfile",
            MemoryKey = "user_name",
            MemoryValue = "Đã hết hạn",
            Confidence = 0.7f,
            IsConfirmed = false,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5),
            CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
            UpdatedAtUtc = DateTime.UtcNow.AddDays(-1)
        };
        _db.AgentMemories.Add(expired);
        _db.SaveChanges();

        var service = CreateService(recallUnconfirmed: true);

        var recalled = await service.RecallMemoriesAsync(_tenantId, _userId, "user_name", ct: CancellationToken.None);

        Assert.Empty(recalled);
    }

    // ── 3. The loop closes through the existing confirm API ──

    [Fact]
    public async Task ConfirmMemory_MakesAnExtractedFactRecallable_WithDefaultSettings()
    {
        var memory = AddMemory(isConfirmed: false, key: "user_name", value: "Lan Anh");
        var service = CreateService(recallUnconfirmed: false);

        Assert.Empty(await service.RecallMemoriesAsync(_tenantId, _userId, "user_name", ct: CancellationToken.None));

        // Exactly what POST /api/memory/{id}/confirm → ConfirmAgentMemoryCommand does.
        Assert.True(await service.ConfirmMemoryAsync(_tenantId, _userId, memory.Id, CancellationToken.None));

        var recalled = await service.RecallMemoriesAsync(_tenantId, _userId, "user_name", ct: CancellationToken.None);
        Assert.Single(recalled);
        Assert.Equal("Lan Anh", recalled[0].Value);
    }

    public void Dispose()
    {
        _modelManager.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Test doubles ──

    /// <summary>Always misses, so every recall exercises the database + filter path.</summary>
    private sealed class NoOpDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => null;
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) { }
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
            => Task.CompletedTask;
    }

    private sealed class StubVectorStore : IVectorStoreService
    {
        public Task UpsertVectorAsync(string collectionName, Guid id, float[] vector, Dictionary<string, object>? payload = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<List<VectorSearchResult>> SearchSimilarWithAnyPayloadAsync(
            string collectionName, float[] queryVector, string payloadField,
            IReadOnlyList<string> allowedValues, int topK, CancellationToken ct = default)
            => Task.FromResult(new List<VectorSearchResult>());

        public Task EnsureCollectionExistsAsync(string collectionName, ulong vectorSize, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteVectorsAsync(string collectionName, IReadOnlyList<Guid> ids, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteByPayloadFilterAsync(string collectionName, string payloadField, string value, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<List<VectorSearchResult>> SearchByPayloadFilterAsync(
            string collectionName, string payloadField, List<string> keywords,
            string tenantFilterField, string tenantId, int topK, CancellationToken ct = default)
            => Task.FromResult(new List<VectorSearchResult>());

        public Task<List<VectorSearchResult>> SearchSimilarWithinDocumentsAsync(
            string collectionName, float[] queryVector, string tenantField, string tenantId,
            string documentIdField, IReadOnlyList<string> documentIds, int topK, CancellationToken ct = default)
            => Task.FromResult(new List<VectorSearchResult>());

        public Task<List<VectorSearchResult>> SearchByPayloadWithinDocumentsAsync(
            string collectionName, string payloadField, List<string> keywords,
            string tenantField, string tenantId, string documentIdField,
            IReadOnlyList<string> documentIds, int topK, CancellationToken ct = default)
            => Task.FromResult(new List<VectorSearchResult>());
    }
}
