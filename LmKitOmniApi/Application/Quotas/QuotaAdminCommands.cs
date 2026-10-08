using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.Quotas;

public sealed class CreatePlanCommand : IRequest<QuotaMutationResult>
{
    public SavePlanRequest Request { get; set; } = new(null, 0, null);
}

public sealed class UpdatePlanCommand : IRequest<QuotaMutationResult>
{
    public Guid Id { get; set; }
    public SavePlanRequest Request { get; set; } = new(null, 0, null);
}

/// <summary>Ngừng dùng gói (soft) — gói cũ vẫn tra cứu được cho các kỳ đã chạy.</summary>
public sealed class DeactivatePlanCommand : IRequest<QuotaMutationResult>
{
    public Guid Id { get; set; }
}

public sealed class AssignPlanCommand : IRequest<QuotaMutationResult>
{
    public Guid TenantId { get; set; }
    public Guid PlanId { get; set; }
    public DateTime? RenewalAtUtc { get; set; }

    /// <summary>Ai gán — chỉ để truy vết.</summary>
    public Guid? ActorUserId { get; set; }
}

/// <summary>Gỡ gói khỏi đơn vị: đơn vị quay về \"không hạn mức\".</summary>
public sealed class RemovePlanCommand : IRequest<QuotaMutationResult>
{
    public Guid TenantId { get; set; }
}

public sealed class CreateGrantCommand : IRequest<QuotaMutationResult>
{
    public Guid TenantId { get; set; }
    public SaveGrantRequest Request { get; set; } = new(0, null, null);
    public Guid? ActorUserId { get; set; }
}

/// <summary>Gỡ một grant CHƯA tiêu đồng nào; grant đã tiêu phải để hết hạn tự nhiên.</summary>
public sealed class DeleteGrantCommand : IRequest<QuotaMutationResult>
{
    public Guid GrantId { get; set; }
}

public sealed class SetTenantCreditCommand : IRequest<QuotaMutationResult>
{
    public Guid TenantId { get; set; }
    public int Balance { get; set; }
}

public sealed class QuotaAdminCommandHandlers :
    IRequestHandler<CreatePlanCommand, QuotaMutationResult>,
    IRequestHandler<UpdatePlanCommand, QuotaMutationResult>,
    IRequestHandler<DeactivatePlanCommand, QuotaMutationResult>,
    IRequestHandler<AssignPlanCommand, QuotaMutationResult>,
    IRequestHandler<RemovePlanCommand, QuotaMutationResult>,
    IRequestHandler<CreateGrantCommand, QuotaMutationResult>,
    IRequestHandler<DeleteGrantCommand, QuotaMutationResult>,
    IRequestHandler<SetTenantCreditCommand, QuotaMutationResult>
{
    private const int MaxPlanNameLength = 200;
    private const int MaxReasonLength = 300;

    /// <summary>Trần một grant, chỉ để chặn giá trị vô lý do gõ nhầm (vd. 900000000000).
    /// Hạn mức gói tháng lớn nhất trong hệ còn xa mức này.</summary>
    private const int MaxGrantTokens = 1_000_000_000;

    private const string TenantNotFound = "Không tìm thấy đơn vị.";
    private const string PlanNotFound = "Không tìm thấy gói.";

    private readonly HermesDbContext _dbContext;

    public QuotaAdminCommandHandlers(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<QuotaMutationResult> Handle(CreatePlanCommand request, CancellationToken cancellationToken)
    {
        var (name, error) = await ValidatePlanNameAsync(request.Request.Name, excludeId: null, cancellationToken);
        if (error is not null) return new QuotaMutationResult(false, error);
        if (request.Request.MonthlyTokenLimit < 0)
            return new QuotaMutationResult(false, "Hạn mức tháng không được âm (0 = không giới hạn).");

        var plan = new Plan
        {
            Name = name!,
            MonthlyTokenLimit = request.Request.MonthlyTokenLimit,
            IsActive = request.Request.IsActive ?? true
        };
        _dbContext.Plans.Add(plan);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return new QuotaMutationResult(true, null, plan.Id);
    }

    public async Task<QuotaMutationResult> Handle(UpdatePlanCommand request, CancellationToken cancellationToken)
    {
        var plan = await _dbContext.Plans.FirstOrDefaultAsync(p => p.Id == request.Id, cancellationToken);
        if (plan is null) return new QuotaMutationResult(false, PlanNotFound, null, IsNotFound: true);

        var (name, error) = await ValidatePlanNameAsync(request.Request.Name, request.Id, cancellationToken);
        if (error is not null) return new QuotaMutationResult(false, error);
        if (request.Request.MonthlyTokenLimit < 0)
            return new QuotaMutationResult(false, "Hạn mức tháng không được âm (0 = không giới hạn).");

        plan.Name = name!;
        plan.MonthlyTokenLimit = request.Request.MonthlyTokenLimit;
        if (request.Request.IsActive is { } isActive) plan.IsActive = isActive;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return new QuotaMutationResult(true, null, plan.Id);
    }

    public async Task<QuotaMutationResult> Handle(DeactivatePlanCommand request, CancellationToken cancellationToken)
    {
        var plan = await _dbContext.Plans.FirstOrDefaultAsync(p => p.Id == request.Id, cancellationToken);
        if (plan is null) return new QuotaMutationResult(false, PlanNotFound, null, IsNotFound: true);
        if (!plan.IsActive) return new QuotaMutationResult(true, null, plan.Id); // đã ngừng — idempotent

        // Chặn "gói biến mất trong khi đơn vị vẫn đang dùng": admin phải chuyển các đơn vị đó
        // sang gói khác TRƯỚC, nếu không hạn mức của họ đổi sau lưng mà không ai biết.
        var inUse = await _dbContext.Subscriptions
            .CountAsync(s => s.PlanId == plan.Id && s.IsActive, cancellationToken);
        if (inUse > 0)
        {
            return new QuotaMutationResult(false,
                $"Gói \"{plan.Name}\" đang được {inUse} đơn vị sử dụng — chuyển họ sang gói khác trước khi ngừng dùng gói này.");
        }

        plan.IsActive = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return new QuotaMutationResult(true, null, plan.Id);
    }

    public async Task<QuotaMutationResult> Handle(AssignPlanCommand request, CancellationToken cancellationToken)
    {
        var tenantExists = await _dbContext.Tenants.AnyAsync(t => t.Id == request.TenantId, cancellationToken);
        if (!tenantExists) return new QuotaMutationResult(false, TenantNotFound, null, IsNotFound: true);

        var plan = await _dbContext.Plans.FirstOrDefaultAsync(p => p.Id == request.PlanId, cancellationToken);
        if (plan is null) return new QuotaMutationResult(false, PlanNotFound, null, IsNotFound: true);
        if (!plan.IsActive)
            return new QuotaMutationResult(false, $"Gói \"{plan.Name}\" đã ngừng dùng — không thể gán cho đơn vị.");

        if (request.RenewalAtUtc is { } renewal && renewal <= DateTime.UtcNow)
            return new QuotaMutationResult(false, "Ngày gia hạn phải ở tương lai.");

        var active = await _dbContext.Subscriptions
            .Where(s => s.TenantId == request.TenantId && s.IsActive)
            .ToListAsync(cancellationToken);

        // Đã đúng gói này rồi: chỉ cập nhật kỳ gia hạn tại chỗ. Tắt rồi tạo lại sẽ làm mất dấu
        // vết "đơn vị đã dùng gói này từ bao giờ" mà không đổi lấy điều gì.
        var current = active.FirstOrDefault(s => s.PlanId == request.PlanId);
        if (current is not null)
        {
            current.RenewalAtUtc = request.RenewalAtUtc;
            // Dữ liệu cũ có thể có nhiều dòng active; dọn luôn để bất biến "một gói active" đúng.
            foreach (var duplicate in active.Where(s => s.Id != current.Id)) duplicate.IsActive = false;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return new QuotaMutationResult(true, null, current.Id);
        }

        // Một gói active cho mỗi đơn vị: tắt dòng đang có rồi thêm dòng mới trong CÙNG một
        // SaveChanges (một transaction), nên không có khoảnh khắc nào đơn vị mang hai hạn mức.
        foreach (var previous in active) previous.IsActive = false;

        var subscription = new Subscription
        {
            TenantId = request.TenantId,
            PlanId = request.PlanId,
            StartedAtUtc = DateTime.UtcNow,
            RenewalAtUtc = request.RenewalAtUtc,
            IsActive = true,
            CreatedByUserId = request.ActorUserId
        };
        _dbContext.Subscriptions.Add(subscription);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return new QuotaMutationResult(true, null, subscription.Id);
    }

    public async Task<QuotaMutationResult> Handle(RemovePlanCommand request, CancellationToken cancellationToken)
    {
        var tenantExists = await _dbContext.Tenants.AnyAsync(t => t.Id == request.TenantId, cancellationToken);
        if (!tenantExists) return new QuotaMutationResult(false, TenantNotFound, null, IsNotFound: true);

        var active = await _dbContext.Subscriptions
            .Where(s => s.TenantId == request.TenantId && s.IsActive)
            .ToListAsync(cancellationToken);

        // Idempotent: đơn vị không có gói thì "gỡ gói" đã là trạng thái mong muốn.
        if (active.Count == 0) return new QuotaMutationResult(true, null, request.TenantId);

        foreach (var subscription in active) subscription.IsActive = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return new QuotaMutationResult(true, null, request.TenantId);
    }

    public async Task<QuotaMutationResult> Handle(CreateGrantCommand request, CancellationToken cancellationToken)
    {
        var tenantExists = await _dbContext.Tenants.AnyAsync(t => t.Id == request.TenantId, cancellationToken);
        if (!tenantExists) return new QuotaMutationResult(false, TenantNotFound, null, IsNotFound: true);

        if (request.Request.Tokens <= 0)
            return new QuotaMutationResult(false, "Số token cấp thêm phải lớn hơn 0.");
        if (request.Request.Tokens > MaxGrantTokens)
            return new QuotaMutationResult(false, $"Số token cấp thêm tối đa {MaxGrantTokens:N0}.");

        var reason = request.Request.Reason?.Trim();
        if (reason is { Length: > MaxReasonLength })
            return new QuotaMutationResult(false, $"Lý do tối đa {MaxReasonLength} ký tự.");

        if (request.Request.ExpiresAtUtc is { } expiresAt && expiresAt <= DateTime.UtcNow)
            return new QuotaMutationResult(false, "Hạn của grant phải ở tương lai (bỏ trống = vô hạn).");

        var grant = new TokenGrant
        {
            TenantId = request.TenantId,
            Tokens = request.Request.Tokens,
            UsedTokens = 0,
            ExpiresAtUtc = request.Request.ExpiresAtUtc,
            Reason = string.IsNullOrEmpty(reason) ? null : reason,
            CreatedByUserId = request.ActorUserId
        };
        _dbContext.TokenGrants.Add(grant);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return new QuotaMutationResult(true, null, grant.Id);
    }

    public async Task<QuotaMutationResult> Handle(DeleteGrantCommand request, CancellationToken cancellationToken)
    {
        var grant = await _dbContext.TokenGrants.FirstOrDefaultAsync(g => g.Id == request.GrantId, cancellationToken);
        if (grant is null)
            return new QuotaMutationResult(false, "Không tìm thấy grant.", null, IsNotFound: true);

        // Grant đã tiêu một phần là dấu vết của hạn mức đã thực sự được dùng; xoá nó là xoá lịch
        // sử chi tiêu. Muốn dừng cấp thêm thì đặt hạn về quá khứ hoặc để nó hết hạn tự nhiên.
        if (grant.UsedTokens > 0)
        {
            return new QuotaMutationResult(false,
                $"Grant đã dùng {grant.UsedTokens:N0} token — không thể gỡ. Để nó hết hạn tự nhiên hoặc gán hạn trong quá khứ.");
        }

        _dbContext.TokenGrants.Remove(grant);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return new QuotaMutationResult(true, null, request.GrantId);
    }

    public async Task<QuotaMutationResult> Handle(SetTenantCreditCommand request, CancellationToken cancellationToken)
    {
        var tenantExists = await _dbContext.Tenants.AnyAsync(t => t.Id == request.TenantId, cancellationToken);
        if (!tenantExists) return new QuotaMutationResult(false, TenantNotFound, null, IsNotFound: true);
        if (request.Balance < 0) return new QuotaMutationResult(false, "Số dư token không được âm.");

        // Đúng một dòng số dư cho mỗi đơn vị (unique index ở tầng DB) → upsert thay vì luôn thêm
        // dòng mới, nếu không khoá unique sẽ ném ở lần thứ hai.
        var credit = await _dbContext.TenantCredits
            .FirstOrDefaultAsync(c => c.TenantId == request.TenantId, cancellationToken);

        if (credit is null)
        {
            credit = new TenantCredit { TenantId = request.TenantId, Balance = request.Balance };
            _dbContext.TenantCredits.Add(credit);
        }
        else
        {
            credit.Balance = request.Balance;
            credit.UpdatedAtUtc = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return new QuotaMutationResult(true, null, credit.Id);
    }

    /// <summary>
    /// Tên gói là duy nhất KHÔNG phân biệt hoa/thường (đúng như index unique của bảng chỉ chặn
    /// trùng tuyệt đối). Cắt khoảng trắng trước khi lưu để " Gói 1K" không tạo ra một gói thứ hai.
    /// </summary>
    private async Task<(string? Name, string? Error)> ValidatePlanNameAsync(
        string? rawName, Guid? excludeId, CancellationToken cancellationToken)
    {
        var name = rawName?.Trim();
        if (string.IsNullOrEmpty(name)) return (null, "Tên gói không được để trống.");
        if (name.Length > MaxPlanNameLength) return (null, $"Tên gói tối đa {MaxPlanNameLength} ký tự.");

        var normalized = name.ToLowerInvariant();
        var duplicated = await _dbContext.Plans
            .AnyAsync(p => p.Name.ToLower() == normalized && (excludeId == null || p.Id != excludeId),
                cancellationToken);
        return duplicated ? (null, "Tên gói đã tồn tại.") : (name, null);
    }
}
