using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

public class ToolSecurityPolicyTests
{
    [Fact]
    public async Task McpMutation_RequiresApprovalForAdmin()
    {
        var permissions = new ToolPermissionService(NullLogger<ToolPermissionService>.Instance);

        var read = await permissions.CanInvokeToolAsync(Guid.NewGuid(), Guid.NewGuid(), "Admin", "MCP:read_issues");
        var delete = await permissions.CanInvokeToolAsync(Guid.NewGuid(), Guid.NewGuid(), "Admin", "MCP:delete_issue");
        var send = await permissions.CanInvokeToolAsync(Guid.NewGuid(), Guid.NewGuid(), "Admin", "MCP:send_email");
        var user = await permissions.CanInvokeToolAsync(Guid.NewGuid(), Guid.NewGuid(), "User", "MCP:read_issues");

        Assert.True(read.IsAllowed);
        Assert.True(delete.RequiresApproval);
        Assert.True(send.RequiresApproval);
        Assert.False(user.IsAllowed);
    }

    [Fact]
    public void FileSandbox_DoesNotAcceptSiblingWithAllowedPrefix()
    {
        var sandbox = new ToolSandboxService(NullLogger<ToolSandboxService>.Instance);
        var sibling = Path.Combine(Directory.GetCurrentDirectory(), "UploadsEvil", "payload.txt");
        var allowed = Path.Combine(Directory.GetCurrentDirectory(), "Uploads", "payload.txt");

        Assert.False(sandbox.ValidateFilePath(sibling).IsAllowed);
        Assert.True(sandbox.ValidateFilePath(allowed).IsAllowed);
    }

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("file:///etc/passwd")]
    public void UrlSandbox_BlocksInternalOrUnsupportedTargets(string url)
    {
        var sandbox = new ToolSandboxService(NullLogger<ToolSandboxService>.Instance);
        Assert.False(sandbox.ValidateUrl(url).IsAllowed);
    }

    [Fact]
    public async Task ToolRateLimit_PersistsAcrossCallsOnSameService()
    {
        var permissions = new ToolPermissionService(NullLogger<ToolPermissionService>.Instance);
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        for (var i = 0; i < 10; i++)
            await permissions.RecordToolInvocationAsync(tenantId, userId, "SearchWeb");

        var result = await permissions.CanInvokeToolAsync(
            tenantId, userId, "User", "SearchWeb");

        Assert.False(result.IsAllowed);
        Assert.Contains("Rate limit", result.DenialReason);
    }
}
