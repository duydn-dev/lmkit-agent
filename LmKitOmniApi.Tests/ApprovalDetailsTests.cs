using LmKitOmniApi.Application.Approvals.Handlers;
using LmKitOmniApi.Application.Approvals.Queries;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pha 3: pending approvals surface the DECRYPTED action payload (e.g. the SQL a
/// write wants to run) so a human can meaningfully approve it — owner-scoped, and
/// never leaking another user's payload.
/// </summary>
public sealed class ApprovalDetailsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly HermesDbContext _db;
    private readonly TaskApprovalPayloadProtector _protector = new(new EphemeralDataProtectionProvider());

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    public ApprovalDetailsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new HermesDbContext(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _db.Tenants.Add(new Tenant { Id = _tenantId, Name = "T" });
        _db.Users.Add(new User { Id = _userId, TenantId = _tenantId, Username = "u", Email = "u@t.test", PasswordHash = "x", Role = "User" });
        var sessionId = Guid.NewGuid();
        _db.ChatSessions.Add(new ChatSession { Id = sessionId, TenantId = _tenantId, UserId = _userId });
        _db.TaskApprovals.Add(new TaskApproval
        {
            TenantId = _tenantId,
            UserId = _userId,
            ChatSessionId = sessionId,
            ActionName = "DBWRITE",
            ParametersJson = _protector.Protect("DELETE FROM customers WHERE id = 1"),
            Status = "Pending"
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Pending_ReturnsDecryptedDetails_ForTheOwner()
    {
        var handler = new GetPendingApprovalsQueryHandler(_db, _protector);
        var result = await handler.Handle(new GetPendingApprovalsQuery { TenantId = _tenantId, UserId = _userId }, CancellationToken.None);

        var item = Assert.Single(result);
        Assert.Equal("DBWRITE", item.ActionName);
        Assert.Equal("DELETE FROM customers WHERE id = 1", item.Details);
    }

    [Fact]
    public async Task Pending_NeverReturnsAnotherUsersApproval()
    {
        var handler = new GetPendingApprovalsQueryHandler(_db, _protector);
        var result = await handler.Handle(new GetPendingApprovalsQuery { TenantId = _tenantId, UserId = Guid.NewGuid() }, CancellationToken.None);
        Assert.Empty(result);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
