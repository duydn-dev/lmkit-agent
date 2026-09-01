using Microsoft.AspNetCore.DataProtection;

namespace LmKitOmniApi.Infrastructure.Security;

public sealed class TaskApprovalPayloadProtector
{
    private const string Prefix = "dp:v1:";
    private readonly IDataProtector _protector;

    public TaskApprovalPayloadProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("LmKitOmniApi.TaskApprovalPayload.v1");
    }

    public string Protect(string payload) => Prefix + _protector.Protect(payload);

    public string Unprotect(string protectedPayload)
    {
        if (!protectedPayload.StartsWith(Prefix, StringComparison.Ordinal))
            return protectedPayload; // compatibility for approvals created before this migration
        return _protector.Unprotect(protectedPayload[Prefix.Length..]);
    }
}
