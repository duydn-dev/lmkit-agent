using LmKitOmniApi.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;

namespace LmKitOmniApi.Tests;

public class TaskApprovalPayloadProtectorTests
{
    [Fact]
    public void Payload_IsEncryptedAndRoundTrips()
    {
        var protector = new TaskApprovalPayloadProtector(new EphemeralDataProtectionProvider());
        const string payload = "{\"recipient\":\"private@example.com\"}";

        var encrypted = protector.Protect(payload);

        Assert.DoesNotContain("private@example.com", encrypted);
        Assert.Equal(payload, protector.Unprotect(encrypted));
    }

    [Fact]
    public void LegacyPlaintextPayload_RemainsExecutable()
    {
        var protector = new TaskApprovalPayloadProtector(new EphemeralDataProtectionProvider());
        Assert.Equal("legacy", protector.Unprotect("legacy"));
    }
}
