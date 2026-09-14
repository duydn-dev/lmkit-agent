using LmKitOmniApi.Application.Schedules.Commands;
using LmKitOmniApi.Application.Schedules.Queries;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.Schedules;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Tool lịch qua hội thoại: schedule_task parse JSON của agent (kể cả
/// timeOfDayUtc "HH:mm" và runAtUtc ISO) rồi đi qua ĐÚNG CreateScheduledTaskCommand;
/// cancel_schedule chỉ TẮT (không xóa), khớp theo id/tên, bắt mơ hồ; mọi lỗi là
/// chuỗi "[Lịch] …" agent đọc được.
/// </summary>
public sealed class ScheduleToolServiceTests : IDisposable
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();

    private readonly SqliteConnection _connection;

    public ScheduleToolServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = TenantId, Name = "T" });
        db.Users.Add(new User
        {
            Id = UserId, TenantId = TenantId, Username = "u", Email = "u@x.test",
            PasswordHash = "h", FullName = "U"
        });
        db.Users.Add(new User
        {
            Id = OtherUserId, TenantId = TenantId, Username = "o", Email = "o@x.test",
            PasswordHash = "h", FullName = "O"
        });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private HermesDbContext CreateContext()
        => new(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);

    /// <summary>Fake IMediator: bắt command tạo lịch và trả kịch bản định sẵn.</summary>
    private sealed class RecordingMediator : IMediator
    {
        public CreateScheduledTaskCommand? LastCreate;
        public SaveScheduledTaskResult CreateResult = SaveScheduledTaskResult.Success(new ScheduledTaskDto
        {
            Id = Guid.NewGuid(),
            Name = "Báo cáo sáng",
            ScheduleKind = "daily",
            TimeOfDayMinutes = 60,
            RunMode = "agent",
            Enabled = true,
            NextRunUtc = DateTime.UtcNow.AddHours(5)
        });
        public Application.Common.PagedResult<ScheduledTaskDto> ListResult = new();

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case CreateScheduledTaskCommand create:
                    LastCreate = create;
                    return Task.FromResult((TResponse)(object)CreateResult);
                case ListScheduledTasksQuery:
                    return Task.FromResult((TResponse)(object)ListResult);
                default:
                    throw new NotSupportedException(request.GetType().Name);
            }
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
            => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification
            => throw new NotSupportedException();
    }

    private (ScheduleToolService Service, RecordingMediator Mediator, HermesDbContext Db) Build()
    {
        var mediator = new RecordingMediator();
        var db = CreateContext();
        var service = new ScheduleToolService(mediator, db, NullLogger<ScheduleToolService>.Instance);
        return (service, mediator, db);
    }

    [Fact]
    public async Task Create_ParsesAgentJson_AndMapsTimeOfDayUtcToMinutes()
    {
        var (service, mediator, db) = Build();
        await using var _ = db;

        var result = await service.CreateAsync(TenantId, UserId,
            """{"name":"Báo cáo sáng","prompt":"Query số liệu hôm qua","kind":"daily","timeOfDayUtc":"01:30","runMode":"agent"}""",
            CancellationToken.None);

        Assert.StartsWith("[Lịch] Đã tạo lịch", result);
        var command = mediator.LastCreate!;
        Assert.Equal(TenantId, command.TenantId);
        Assert.Equal(UserId, command.UserId);
        Assert.Equal("daily", command.ScheduleKind);
        Assert.Equal(90, command.TimeOfDayMinutes); // 01:30 UTC
        Assert.Equal("agent", command.RunMode);
    }

    [Fact]
    public async Task Create_Once_ParsesIsoRunAtUtc()
    {
        var (service, mediator, db) = Build();
        await using var _ = db;

        await service.CreateAsync(TenantId, UserId,
            """{"name":"Nhắc một lần","prompt":"x","kind":"once","runAtUtc":"2026-10-01T01:00:00Z"}""",
            CancellationToken.None);

        Assert.Equal("once", mediator.LastCreate!.ScheduleKind);
        Assert.Equal(new DateTime(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc), mediator.LastCreate.RunAtUtc);
    }

    [Theory]
    [InlineData("chạy mỗi sáng nhé", "JSON")]                       // không phải JSON
    [InlineData("""{"prompt":"x","kind":"daily"}""", "name")]        // thiếu tên
    [InlineData("""{"name":"a","prompt":"x","kind":"once","runAtUtc":"mai nhé"}""", "runAtUtc")] // ISO hỏng
    [InlineData("""{"name":"a","prompt":"x","kind":"daily","timeOfDayUtc":"25:99"}""", "timeOfDayUtc")]
    public async Task Create_BadInput_ReturnsAgentReadableError_WithoutSending(string input, string expectedHint)
    {
        var (service, mediator, db) = Build();
        await using var _ = db;

        var result = await service.CreateAsync(TenantId, UserId, input, CancellationToken.None);

        Assert.StartsWith("[Lịch]", result);
        Assert.Contains(expectedHint, result);
        Assert.Null(mediator.LastCreate); // lỗi parse thì command không bao giờ được gửi
    }

    [Fact]
    public async Task Create_WhenValidationRefuses_SurfacesTheVietnameseMessage()
    {
        var (service, mediator, db) = Build();
        await using var _ = db;
        mediator.CreateResult = SaveScheduledTaskResult.ValidationFailed("Mỗi người dùng chỉ được bật tối đa 10 lịch tự động.");

        var result = await service.CreateAsync(TenantId, UserId,
            """{"name":"a","prompt":"x","kind":"daily","timeOfDayUtc":"01:00"}""", CancellationToken.None);

        Assert.Contains("tối đa 10 lịch", result);
    }

    // ── cancel_schedule ─────────────────────────────────────────────────

    private async Task<ScheduledTask> SeedTaskAsync(string name, bool enabled = true, Guid? userId = null)
    {
        await using var db = CreateContext();
        var task = new ScheduledTask
        {
            TenantId = TenantId,
            UserId = userId ?? UserId,
            Name = name,
            Prompt = "x",
            ScheduleKind = "daily",
            TimeOfDayMinutes = 60,
            Enabled = enabled,
            NextRunUtc = DateTime.UtcNow.AddHours(1)
        };
        db.ScheduledTasks.Add(task);
        await db.SaveChangesAsync();
        return task;
    }

    [Fact]
    public async Task Cancel_ByName_DisablesButNeverDeletes()
    {
        var task = await SeedTaskAsync("Báo cáo quan trắc sáng");
        var (service, _, db) = Build();
        await using var _ = db;

        var result = await service.CancelAsync(TenantId, UserId, "quan trắc", CancellationToken.None);

        Assert.Contains("Đã tắt lịch", result);
        await using var verify = CreateContext();
        var stored = await verify.ScheduledTasks.SingleAsync(t => t.Id == task.Id);
        Assert.False(stored.Enabled); // tắt, không xóa
    }

    [Fact]
    public async Task Cancel_ByShortId_Works_AndAmbiguousNamesAskForTheId()
    {
        var first = await SeedTaskAsync("Báo cáo sáng");
        await SeedTaskAsync("Báo cáo chiều");
        var (service, _, db) = Build();
        await using var _ = db;

        var ambiguous = await service.CancelAsync(TenantId, UserId, "Báo cáo", CancellationToken.None);
        Assert.Contains("2 lịch khớp", ambiguous);

        var byId = await service.CancelAsync(TenantId, UserId, first.Id.ToString("N")[..8], CancellationToken.None);
        Assert.Contains("Đã tắt lịch", byId);
    }

    [Fact]
    public async Task Cancel_NeverTouchesAnotherUsersSchedule()
    {
        var foreign = await SeedTaskAsync("Lịch của người khác", userId: OtherUserId);
        var (service, _, db) = Build();
        await using var _ = db;

        var result = await service.CancelAsync(TenantId, UserId, "người khác", CancellationToken.None);

        Assert.Contains("Không tìm thấy", result);
        await using var verify = CreateContext();
        Assert.True((await verify.ScheduledTasks.SingleAsync(t => t.Id == foreign.Id)).Enabled);
    }
}
