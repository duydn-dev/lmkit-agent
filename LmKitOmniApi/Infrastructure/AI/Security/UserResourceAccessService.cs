namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>Maps authenticated tenant/user identities to an isolated upload root.</summary>
public sealed class UserResourceAccessService
{
    private readonly ToolSandboxService _sandbox;

    public UserResourceAccessService(ToolSandboxService sandbox) => _sandbox = sandbox;

    public string GetUploadDirectory(Guid tenantId, Guid userId) => Path.Combine(
        Directory.GetCurrentDirectory(),
        "Uploads",
        tenantId.ToString("N"),
        userId.ToString("N"));

    public PathValidationResult ValidateOwnedPath(Guid tenantId, Guid userId, string requestedPath)
    {
        var sandboxResult = _sandbox.ValidateFilePath(requestedPath);
        if (!sandboxResult.IsAllowed) return sandboxResult;

        var ownerRoot = Path.GetFullPath(GetUploadDirectory(tenantId, userId));
        var relative = Path.GetRelativePath(ownerRoot, sandboxResult.SanitizedPath);
        var owned = relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);

        return owned
            ? sandboxResult
            : PathValidationResult.Deny("Resource does not belong to the authenticated user.");
    }
}
