using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.ToolPolicy;
using LmKitOmniApi.Domain.Constants;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Bảng phân loại rủi ro là NGUỒN SỰ THẬT DUY NHẤT của chính sách tool, nên nó phải tự bảo vệ mình:
/// mọi thay đổi phân loại làm đổi hành vi duyệt phải làm đỏ một test ở đây, chứ không lặng lẽ đổi
/// quyền của mọi đơn vị.
/// </summary>
public class ToolRiskCatalogTests
{
    [Theory]
    [InlineData("DbWrite", ToolRiskTiers.Write)]
    [InlineData("CallApiWrite", ToolRiskTiers.Write)]
    [InlineData("BrowseWeb", ToolRiskTiers.Write)]
    [InlineData("ScheduleTask", ToolRiskTiers.Write)]
    [InlineData("IngestDocument", ToolRiskTiers.Write)]
    [InlineData("DeleteDocument", ToolRiskTiers.Sensitive)]
    [InlineData("DbQuery", ToolRiskTiers.Read)]
    [InlineData("FetchWeb", ToolRiskTiers.Read)]
    [InlineData("RunPython", ToolRiskTiers.Read)]
    [InlineData("ScheduleManage", ToolRiskTiers.Read)]
    public void TierOf_ClassifiesDeclaredTools(string toolName, string expectedTier)
        => Assert.Equal(expectedTier, ToolRiskCatalog.TierOf(toolName));

    [Fact]
    public void TierOf_IsCaseInsensitive()
        => Assert.Equal(ToolRiskTiers.Write, ToolRiskCatalog.TierOf("dbwrite"));

    [Fact]
    public void TierOf_UnknownTool_IsSensitive_FailClosed()
    {
        // Tool lạ KHÔNG được rơi về Read: một tool mới được thêm vào danh sách vai trò mà quên khai
        // báo mức rủi ro thì phải chờ người duyệt, không được tự chạy.
        Assert.Equal(ToolRiskTiers.Sensitive, ToolRiskCatalog.TierOf("SomeBrandNewTool"));
        Assert.Equal(ToolRiskTiers.Sensitive, ToolRiskCatalog.TierOf(""));
        Assert.Equal(ToolRiskTiers.Sensitive, ToolRiskCatalog.TierOf(null!));
    }

    [Fact]
    public void TierOf_McpToolsAreSensitive_UnlessTrustedRead()
    {
        // Annotation read-only của server ngoài là gợi ý không đáng tin: mặc định phải duyệt.
        Assert.Equal(ToolRiskTiers.Sensitive, ToolRiskCatalog.TierOf("MCP:issues:manage"));
        Assert.Equal(ToolRiskTiers.Sensitive, ToolRiskCatalog.TierOf("MCP:issues:read_issues"));
        // Chỉ khi admin đơn vị đã tin cậy server đó thì tool read-only mới thành Read.
        Assert.Equal(ToolRiskTiers.Read, ToolRiskCatalog.TierOf("MCP:TRUSTED_READ:issues:read_issues"));
    }

    [Fact]
    public void ApprovalRequiredTools_MatchesTheHistoricalApprovalSet()
    {
        // Bộ này THAY THẾ HashSet viết tay trước đây. Hành vi phải giữ nguyên: một tool lọt ra khỏi
        // đây (hoặc lọt vào) là đổi quyền của mọi đơn vị mà không ai xin.
        var expected = new[] { "IngestDocument", "DeleteDocument", "DbWrite", "BrowseWeb", "CallApiWrite", "ScheduleTask" };
        Assert.Equal(
            expected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            ToolRiskCatalog.ApprovalRequiredTools.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        // Và nó phải khớp với mức mặc định suy ra từ phân loại (đúng một nguồn).
        foreach (var tool in ToolRiskCatalog.All)
        {
            var expectedApproval = ToolPolicyEnforcement.DefaultForTier(tool.Tier) == ToolPolicyEnforcement.Approval;
            Assert.Equal(expectedApproval, ToolRiskCatalog.ApprovalRequiredTools.Contains(tool.ToolName));
        }
    }

    [Fact]
    public void EveryCatalogEntry_CarriesAWhy()
    {
        // Mỗi dòng phân loại phải giải thích được vì sao — màn quản trị hiển thị lý do này, và một
        // dòng không lý do là dòng sẽ bị sửa sai bởi người kế tiếp.
        Assert.All(ToolRiskCatalog.All, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Why)));
    }
}

/// <summary>Thứ tự nghiêm ngặt — mọi quy tắc chính sách đều dựa vào nó, nên nó phải khoá bằng test.</summary>
public class ToolPolicyEnforcementTests
{
    [Fact]
    public void Rank_OrdersAutoBelowApprovalBelowBlocked()
    {
        Assert.True(ToolPolicyEnforcement.Rank(ToolPolicyEnforcement.Auto)
                    < ToolPolicyEnforcement.Rank(ToolPolicyEnforcement.Approval));
        Assert.True(ToolPolicyEnforcement.Rank(ToolPolicyEnforcement.Approval)
                    < ToolPolicyEnforcement.Rank(ToolPolicyEnforcement.Blocked));
    }

    [Fact]
    public void Rank_OfAnUnknownValue_IsBlockedNotAuto()
        => Assert.Equal(
            ToolPolicyEnforcement.Rank(ToolPolicyEnforcement.Blocked),
            ToolPolicyEnforcement.Rank("TotallyInvalid"));

    [Fact]
    public void Strictest_PicksTheMoreRestrictiveValue()
    {
        Assert.Equal(ToolPolicyEnforcement.Blocked,
            ToolPolicyEnforcement.Strictest(ToolPolicyEnforcement.Auto, ToolPolicyEnforcement.Blocked));
        Assert.Equal(ToolPolicyEnforcement.Approval,
            ToolPolicyEnforcement.Strictest(ToolPolicyEnforcement.Approval, ToolPolicyEnforcement.Auto));
        // Giá trị lạ đứng về phía chặt: dữ liệu hỏng không được nới quyền.
        Assert.Equal(ToolPolicyEnforcement.Blocked,
            ToolPolicyEnforcement.Strictest(ToolPolicyEnforcement.Auto, "garbage"));
    }

    [Fact]
    public void TryParse_AcceptsOnlyTheThreeSpellings()
    {
        Assert.Equal(ToolPolicyEnforcement.Approval, ToolPolicyEnforcement.TryParse("approval"));
        Assert.Equal(ToolPolicyEnforcement.Blocked, ToolPolicyEnforcement.TryParse(" BLOCKED "));
        Assert.Null(ToolPolicyEnforcement.TryParse("Allow"));
        Assert.Null(ToolPolicyEnforcement.TryParse(null));
    }

    [Theory]
    [InlineData(ToolRiskTiers.Read, ToolPolicyEnforcement.Auto)]
    [InlineData(ToolRiskTiers.Write, ToolPolicyEnforcement.Approval)]
    [InlineData(ToolRiskTiers.Sensitive, ToolPolicyEnforcement.Approval)]
    [InlineData("SomethingElse", ToolPolicyEnforcement.Approval)]
    public void DefaultForTier_MapsRiskToEnforcement(string tier, string expected)
        => Assert.Equal(expected, ToolPolicyEnforcement.DefaultForTier(tier));
}

/// <summary>Luật "chỉ siết, không nới" — thứ bảo vệ khách khỏi một cú click sai trong màn quản trị.</summary>
public class ToolPolicyMutationTests
{
    [Theory]
    [InlineData("DbQuery", ToolPolicyEnforcement.Approval)]
    [InlineData("DbQuery", ToolPolicyEnforcement.Blocked)]
    [InlineData("DbWrite", ToolPolicyEnforcement.Blocked)]
    public void Validate_AllowsTightening(string tool, string requested)
    {
        var result = ToolPolicyMutation.Validate(tool, requested);

        Assert.True(result.IsAllowed);
        Assert.Equal(requested, result.Enforcement);
    }

    [Fact]
    public void Validate_AllowsRestatingTheDefault()
    {
        var result = ToolPolicyMutation.Validate("DbWrite", ToolPolicyEnforcement.Approval);

        Assert.True(result.IsAllowed);
    }

    [Theory]
    [InlineData("DbWrite", ToolPolicyEnforcement.Auto)]
    [InlineData("CallApiWrite", ToolPolicyEnforcement.Auto)]
    [InlineData("BrowseWeb", ToolPolicyEnforcement.Auto)]
    public void Validate_RejectsLoosening(string tool, string requested)
    {
        var result = ToolPolicyMutation.Validate(tool, requested);

        Assert.False(result.IsAllowed);
        Assert.Contains("siết", result.RejectionReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsUnknownTool()
    {
        // Mức rủi ro của một tool là quyết định về sản phẩm, phải sửa trong mã nguồn có review —
        // không đặt từ API, và không để lại dòng cấu hình vô chủ khi tool động (MCP) đổi tên.
        var result = ToolPolicyMutation.Validate("MCP:issues:read_issues", ToolPolicyEnforcement.Blocked);

        Assert.False(result.IsAllowed);
        Assert.Contains("bảng phân loại", result.RejectionReason!);
    }

    [Fact]
    public void Validate_RejectsInvalidEnforcementValue()
    {
        var result = ToolPolicyMutation.Validate("DbQuery", "AllowIt");

        Assert.False(result.IsAllowed);
        Assert.Contains("không hợp lệ", result.RejectionReason!);
    }
}

/// <summary>Áp cấu hình lên mức mặc định — kể cả khi dữ liệu trong DB đã bị sửa sai.</summary>
public class ToolRiskDecisionTests
{
    [Fact]
    public void Default_ComesFromTheCatalog()
    {
        var decision = ToolRiskDecision.Default("DbWrite");

        Assert.Equal(ToolRiskTiers.Write, decision.Tier);
        Assert.Equal(ToolPolicyEnforcement.Approval, decision.Enforcement);
        Assert.Equal("default", decision.Source);
        Assert.True(decision.RequiresApproval);
        Assert.False(decision.IsBlocked);
    }

    [Fact]
    public void Apply_WithNoOverride_KeepsTheBaseline()
    {
        var baseline = ToolRiskDecision.Default("DbQuery");

        Assert.Equal(ToolPolicyEnforcement.Auto, ToolRiskDecision.Apply(baseline, null, null).Enforcement);
        Assert.Equal(ToolPolicyEnforcement.Auto, ToolRiskDecision.Apply(baseline, "  ", null).Enforcement);
        Assert.Equal(ToolPolicyEnforcement.Auto, ToolRiskDecision.Apply(baseline, "Nonsense", null).Enforcement);
    }

    [Fact]
    public void Apply_WithStricterOverride_TightensAndAttributesTheSource()
    {
        var baseline = ToolRiskDecision.Default("DbQuery");

        var decision = ToolRiskDecision.Apply(baseline, ToolPolicyEnforcement.Blocked, "không dùng CSDL ngoài");

        Assert.Equal(ToolPolicyEnforcement.Blocked, decision.Enforcement);
        Assert.Equal("tenant-override", decision.Source);
        Assert.Equal("không dùng CSDL ngoài", decision.Reason);
        Assert.True(decision.IsBlocked);
    }

    [Fact]
    public void Apply_WithLooserOverrideInTheDatabase_IgnoresIt()
    {
        // Phòng thủ theo chiều sâu: Validate đã chặn ghi, nhưng một dòng có thể vào DB bằng đường
        // khác (restore backup, sửa tay, migrate). Đường ĐỌC vì thế vẫn lấy giá trị nghiêm hơn.
        var baseline = ToolRiskDecision.Default("DbWrite");

        var decision = ToolRiskDecision.Apply(baseline, ToolPolicyEnforcement.Auto, "ai đó sửa tay");

        Assert.Equal(ToolPolicyEnforcement.Approval, decision.Enforcement);
        Assert.Equal("default", decision.Source);
    }
}

/// <summary>
/// CRUD cấu hình chính sách (đường admin dùng): đặt, siết tiếp, bỏ. Bất biến quan trọng nhất là
/// "chỉ siết, không nới" — nó phải bị chặn ở TẦNG GHI, không phải chỉ ở UI.
/// </summary>
[Collection("DbSqlite")]
public sealed class ToolPolicyAdminTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private static readonly CancellationToken None = CancellationToken.None;

    public ToolPolicyAdminTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging(); // ToolRiskPolicy ghi log khi không đọc được chính sách
        services.AddDbContext<HermesDbContext>(options => options.UseSqlite(_connection));
        services.AddSingleton<IToolRiskPolicy, ToolRiskPolicy>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = _tenantId, Name = "T" });
        db.SaveChanges();
    }

    private HermesDbContext NewContext()
        => new(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);

    private IToolRiskPolicy Policy() => _provider.GetRequiredService<IToolRiskPolicy>();

    private Task<ToolPolicyWriteResult> SetAsync(string tool, string enforcement, string? reason = null)
        => new SetToolPolicyCommandHandler(NewContext(), Policy()).Handle(new SetToolPolicyCommand
        {
            TenantId = _tenantId, UserId = _userId, ToolName = tool, Enforcement = enforcement, Reason = reason
        }, None);

    private Task<ToolPolicyWriteResult> ClearAsync(string tool)
        => new ClearToolPolicyCommandHandler(NewContext(), Policy())
            .Handle(new ClearToolPolicyCommand { TenantId = _tenantId, ToolName = tool }, None);

    private Task<List<ToolPolicyEntryDto>> ListAsync()
        => new ListToolPoliciesQueryHandler(NewContext())
            .Handle(new ListToolPoliciesQuery { TenantId = _tenantId }, None);

    [Fact]
    public async Task List_WithoutOverrides_ShowsCatalogDefaultsForEveryTool()
    {
        var entries = await ListAsync();

        Assert.Equal(ToolRiskCatalog.All.Count, entries.Count);
        Assert.All(entries, e => Assert.Equal("default", e.Source));
        Assert.Equal(
            ToolPolicyEnforcement.Approval,
            entries.Single(e => e.ToolName == "DbWrite").EffectiveEnforcement);
        Assert.Equal(
            ToolPolicyEnforcement.Auto,
            entries.Single(e => e.ToolName == "DbQuery").EffectiveEnforcement);
    }

    [Fact]
    public async Task Set_TightensAndIsVisibleInTheList()
    {
        var result = await SetAsync("DbQuery", ToolPolicyEnforcement.Blocked, "không dùng CSDL ngoài");

        Assert.Equal("ok", result.Status);
        Assert.Equal(ToolPolicyEnforcement.Blocked, result.Entry!.EffectiveEnforcement);
        Assert.Equal("tenant-override", result.Entry.Source);

        var entry = (await ListAsync()).Single(e => e.ToolName == "DbQuery");
        Assert.Equal(ToolPolicyEnforcement.Blocked, entry.EffectiveEnforcement);
        Assert.Equal(ToolPolicyEnforcement.Auto, entry.BaselineEnforcement);
        Assert.Equal("không dùng CSDL ngoài", entry.Reason);
    }

    [Fact]
    public async Task Set_TwiceForTheSameTool_UpdatesTheOneRow()
    {
        // Unique index (TenantId, ToolName) là thứ giữ cho chính sách xác định: hai dòng cho cùng
        // một tool sẽ khiến lần đọc sau lấy dòng nào tuỳ thứ tự.
        await SetAsync("DbQuery", ToolPolicyEnforcement.Approval);
        await SetAsync("DbQuery", ToolPolicyEnforcement.Blocked);

        using var db = NewContext();
        var rows = db.TenantToolPolicies.Where(p => p.TenantId == _tenantId).ToList();
        var row = Assert.Single(rows);
        Assert.Equal(ToolPolicyEnforcement.Blocked, row.Enforcement);
    }

    [Fact]
    public async Task Set_CannotLoosenALeadingTool()
    {
        var result = await SetAsync("DbWrite", ToolPolicyEnforcement.Auto);

        Assert.Equal("invalid", result.Status);
        Assert.Contains("siết", result.Error!, StringComparison.OrdinalIgnoreCase);

        using var db = NewContext();
        Assert.Empty(db.TenantToolPolicies.Where(p => p.TenantId == _tenantId));
    }

    [Fact]
    public async Task Clear_ReturnsTheToolToTheSystemDefault()
    {
        await SetAsync("BrowseWeb", ToolPolicyEnforcement.Blocked);

        var cleared = await ClearAsync("BrowseWeb");

        Assert.Equal("ok", cleared.Status);
        Assert.Equal(ToolPolicyEnforcement.Approval, cleared.Entry!.EffectiveEnforcement);
        Assert.Equal("default", cleared.Entry.Source);
    }

    [Fact]
    public async Task Clear_UnknownTool_IsNotFound()
        => Assert.Equal("not-found", (await ClearAsync("NoSuchTool")).Status);

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }
}

/// <summary>
/// Chính sách đọc từ DB thật (SQLite in-memory) — kiểm cả chiều cache và chiều "DB lỗi thì chạy mức
/// mặc định".
/// </summary>
[Collection("DbSqlite")]
public sealed class ToolRiskPolicyDatabaseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly Guid _tenantId = Guid.NewGuid();

    public ToolRiskPolicyDatabaseTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<HermesDbContext>(options => options.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = _tenantId, Name = "T" });
        db.SaveChanges();
    }

    private ToolRiskPolicy CreatePolicy() => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<ToolRiskPolicy>.Instance);

    private void SeedOverride(string toolName, string enforcement, string? reason = null)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        db.TenantToolPolicies.Add(new TenantToolPolicy
        {
            TenantId = _tenantId,
            ToolName = toolName,
            Enforcement = enforcement,
            Reason = reason
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task ResolveAsync_WithoutOverrides_ReturnsCatalogDefaults()
    {
        var policy = CreatePolicy();

        Assert.Equal(ToolPolicyEnforcement.Auto, (await policy.ResolveAsync(_tenantId, "DbQuery")).Enforcement);
        Assert.Equal(ToolPolicyEnforcement.Approval, (await policy.ResolveAsync(_tenantId, "DbWrite")).Enforcement);
    }

    [Fact]
    public async Task ResolveAsync_WithTenantOverride_AppliesIt()
    {
        SeedOverride("DbQuery", ToolPolicyEnforcement.Approval, "đơn vị yêu cầu duyệt mọi truy vấn");
        var policy = CreatePolicy();

        var decision = await policy.ResolveAsync(_tenantId, "DbQuery");

        Assert.Equal(ToolPolicyEnforcement.Approval, decision.Enforcement);
        Assert.Equal("tenant-override", decision.Source);
        Assert.Equal("đơn vị yêu cầu duyệt mọi truy vấn", decision.Reason);
    }

    [Fact]
    public async Task ResolveAsync_OverrideIsScopedToItsTenant()
    {
        SeedOverride("DbQuery", ToolPolicyEnforcement.Blocked);
        var policy = CreatePolicy();

        var other = await policy.ResolveAsync(Guid.NewGuid(), "DbQuery");

        Assert.Equal(ToolPolicyEnforcement.Auto, other.Enforcement);
    }

    [Fact]
    public async Task ResolveAsync_Invalidate_MakesANewOverrideVisibleImmediately()
    {
        var policy = CreatePolicy();
        Assert.Equal(ToolPolicyEnforcement.Auto, (await policy.ResolveAsync(_tenantId, "DbQuery")).Enforcement);

        SeedOverride("DbQuery", ToolPolicyEnforcement.Blocked);
        // Chưa xoá cache → replica này còn thấy giá trị cũ (đánh đổi có chủ đích, xem ToolRiskPolicy).
        Assert.Equal(ToolPolicyEnforcement.Auto, (await policy.ResolveAsync(_tenantId, "DbQuery")).Enforcement);

        policy.Invalidate(_tenantId);

        Assert.Equal(ToolPolicyEnforcement.Blocked, (await policy.ResolveAsync(_tenantId, "DbQuery")).Enforcement);
    }

    [Fact]
    public async Task ResolveAsync_WhenTheDatabaseIsUnreachable_FallsBackToDefaults()
    {
        var policy = CreatePolicy();
        _connection.Close(); // DB biến mất sau khi service đã được dựng

        var decision = await policy.ResolveAsync(_tenantId, "DbWrite");

        Assert.Equal(ToolPolicyEnforcement.Approval, decision.Enforcement);
    }

    [Fact]
    public async Task ListOverridesAsync_ReturnsWhatIsStored()
    {
        SeedOverride("DbQuery", ToolPolicyEnforcement.Approval);
        SeedOverride("BrowseWeb", ToolPolicyEnforcement.Blocked);

        var overrides = await CreatePolicy().ListOverridesAsync(_tenantId);

        Assert.Equal(2, overrides.Count);
        Assert.Equal(ToolPolicyEnforcement.Blocked, overrides["BrowseWeb"].Enforcement);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }
}

/// <summary>
/// Đường nối vào cửa kiểm tra quyền: chính sách phải thật sự đổi được quyết định cuối cùng, và host
/// không đăng ký chính sách phải giữ NGUYÊN hành vi cũ.
/// </summary>
public class ToolPermissionRiskPolicyWiringTests
{
    private sealed class FakePolicy : IToolRiskPolicy
    {
        private readonly string _enforcement;

        public FakePolicy(string enforcement) => _enforcement = enforcement;

        public Task<ToolRiskDecision> ResolveAsync(Guid tenantId, string toolName, CancellationToken ct = default)
            => Task.FromResult(ToolRiskDecision.Apply(
                ToolRiskDecision.Default(toolName), _enforcement, "test"));

        public Task<IReadOnlyDictionary<string, TenantToolPolicy>> ListOverridesAsync(
            Guid tenantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, TenantToolPolicy>>(
                new Dictionary<string, TenantToolPolicy>());

        public void Invalidate(Guid tenantId) { }
    }

    private sealed class ThrowingPolicy : IToolRiskPolicy
    {
        public Task<ToolRiskDecision> ResolveAsync(Guid tenantId, string toolName, CancellationToken ct = default)
            => throw new InvalidOperationException("policy store down");

        public Task<IReadOnlyDictionary<string, TenantToolPolicy>> ListOverridesAsync(
            Guid tenantId, CancellationToken ct = default)
            => throw new InvalidOperationException("policy store down");

        public void Invalidate(Guid tenantId) { }
    }

    [Fact]
    public async Task ReadTool_WithTenantApprovalRequirement_NeedsApproval()
    {
        var service = new ToolPermissionService(
            NullLogger<ToolPermissionService>.Instance, redis: null, riskPolicy: new FakePolicy(ToolPolicyEnforcement.Approval));

        var result = await service.CanInvokeToolAsync(Guid.NewGuid(), Guid.NewGuid(), "User", "DbQuery");

        Assert.False(result.IsAllowed);
        Assert.True(result.RequiresApproval);
    }

    [Fact]
    public async Task ToolBlockedByTenantPolicy_IsDenied_NotSentToApproval()
    {
        var service = new ToolPermissionService(
            NullLogger<ToolPermissionService>.Instance, redis: null, riskPolicy: new FakePolicy(ToolPolicyEnforcement.Blocked));

        var result = await service.CanInvokeToolAsync(Guid.NewGuid(), Guid.NewGuid(), "Admin", "BrowseWeb");

        Assert.False(result.IsAllowed);
        Assert.False(result.RequiresApproval);
        Assert.Contains("chặn", result.DenialReason!);
    }

    [Fact]
    public async Task PolicyStoreFailure_DoesNotGiveMoreAuthority()
    {
        var service = new ToolPermissionService(
            NullLogger<ToolPermissionService>.Instance, redis: null, riskPolicy: new ThrowingPolicy());

        // DbWrite vẫn phải chờ duyệt như mức mặc định, dù kho chính sách lỗi.
        var write = await service.CanInvokeToolAsync(Guid.NewGuid(), Guid.NewGuid(), "User", "DbWrite");
        Assert.True(write.RequiresApproval);

        // Và công cụ chỉ-đọc không bị biến thành cần duyệt.
        var read = await service.CanInvokeToolAsync(Guid.NewGuid(), Guid.NewGuid(), "User", "AnalyzeText");
        Assert.True(read.IsAllowed);
    }

    [Fact]
    public async Task WithoutAnyPolicy_InjectedBehaviourMatchesTheCatalog()
    {
        // Host cũ / test dựng tay (không truyền chính sách) phải chạy y hệt trước đây.
        var service = new ToolPermissionService(NullLogger<ToolPermissionService>.Instance);

        Assert.True((await service.CanInvokeToolAsync(Guid.NewGuid(), Guid.NewGuid(), "User", "DbWrite")).RequiresApproval);
        Assert.True((await service.CanInvokeToolAsync(Guid.NewGuid(), Guid.NewGuid(), "User", "FetchWeb")).IsAllowed);
    }

    [Fact]
    public async Task ScheduledWriteBypass_IgnoresAnApprovalPolicy_ButNotABlockedOne()
    {
        // Cấp phép theo lịch chủ đích chỉ bỏ qua yêu cầu duyệt tương tác. Chặn hẳn của đơn vị thì
        // KHÔNG được bỏ qua: đó là chính sách, không phải một phiếu duyệt bị thiếu.
        var approving = new ToolPermissionService(
            NullLogger<ToolPermissionService>.Instance, redis: null, riskPolicy: new FakePolicy(ToolPolicyEnforcement.Approval));
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        Assert.True((await approving.CanInvokeScheduledWriteAsync(tenantId, userId, "User", "DbWrite")).IsAllowed);

        var blocking = new ToolPermissionService(
            NullLogger<ToolPermissionService>.Instance, redis: null, riskPolicy: new FakePolicy(ToolPolicyEnforcement.Blocked));
        var blocked = await blocking.CanInvokeScheduledWriteAsync(tenantId, userId, "User", "DbWrite");

        Assert.False(blocked.IsAllowed);
        Assert.False(blocked.RequiresApproval);
    }
}
