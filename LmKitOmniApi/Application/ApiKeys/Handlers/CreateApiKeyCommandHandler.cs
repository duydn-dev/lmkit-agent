using LmKitOmniApi.Application.ApiKeys.Commands;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.ApiKeys.Handlers;

public sealed class CreateApiKeyCommandHandler : IRequestHandler<CreateApiKeyCommand, CreateApiKeyResult>
{
    private readonly HermesDbContext _db;

    public CreateApiKeyCommandHandler(HermesDbContext db)
    {
        _db = db;
    }

    public async Task<CreateApiKeyResult> Handle(CreateApiKeyCommand request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > ApiKeyRules.MaxNameLength)
            return Invalid($"Tên khóa API là bắt buộc và tối đa {ApiKeyRules.MaxNameLength} ký tự.");

        var expiresInDays = request.ExpiresInDays ?? ApiKeyRules.DefaultExpiresInDays;
        if (expiresInDays is < ApiKeyRules.MinExpiresInDays or > ApiKeyRules.MaxExpiresInDays)
            return Invalid($"Thời hạn khóa (expiresInDays) phải nằm trong khoảng {ApiKeyRules.MinExpiresInDays} đến {ApiKeyRules.MaxExpiresInDays} ngày.");

        var maxRequests = request.MaxRequests ?? 0;
        if (maxRequests is < 0 or > ApiKeyRules.MaxRequestBudget)
            return Invalid("Giới hạn yêu cầu (maxRequests) phải nằm trong khoảng 0 (không giới hạn) đến 1.000.000.");

        var now = DateTime.UtcNow;
        var activeKeyCount = await _db.TenantApiKeys.CountAsync(
            key => key.TenantId == request.TenantId
                && key.UserId == request.UserId
                && key.RevokedAtUtc == null
                && key.ExpiresAtUtc > now,
            cancellationToken);
        if (activeKeyCount >= ApiKeyRules.MaxActiveKeysPerUser)
            return Invalid($"Mỗi người dùng chỉ được giữ tối đa {ApiKeyRules.MaxActiveKeysPerUser} khóa API đang hoạt động. Vui lòng thu hồi bớt khóa cũ trước khi tạo khóa mới.");

        // Raw secret is minted here, returned once, and only its hash is stored.
        var rawKey = ApiKeySecret.Generate();
        var entity = new TenantApiKey
        {
            TenantId = request.TenantId,
            UserId = request.UserId,
            Name = name,
            ApiKey = ApiKeySecret.Hash(rawKey),
            MaxRequests = maxRequests,
            UsedRequests = 0,
            ExpiresAtUtc = now.AddDays(expiresInDays),
            CreatedAtUtc = now
        };
        _db.TenantApiKeys.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        return new CreateApiKeyResult
        {
            Status = ApiKeyMutationStatus.Success,
            Key = new CreatedApiKeyDto
            {
                Id = entity.Id,
                Name = entity.Name,
                RawKey = rawKey,
                ExpiresAtUtc = entity.ExpiresAtUtc
            }
        };
    }

    private static CreateApiKeyResult Invalid(string message) => new()
    {
        Status = ApiKeyMutationStatus.ValidationFailed,
        ErrorMessage = message
    };
}
