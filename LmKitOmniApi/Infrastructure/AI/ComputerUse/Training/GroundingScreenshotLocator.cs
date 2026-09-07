using LmKitOmniApi.Infrastructure.AI.Security;

namespace LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;

/// <summary>
/// Resolves a recorded <see cref="GroundingSample.ScreenshotFileId"/> back to a file on disk
/// so a grounding sample can be trained WITH the picture the model actually saw.
///
/// Why this exists: the computer-use executor harvests each step's screenshot into the
/// <b>owner-scoped</b> upload root (<c>Uploads/&lt;tenant&gt;/&lt;user&gt;/&lt;guid&gt;.png</c>, see
/// <c>ComputerUseExecutor.HarvestScreenshotAsync</c>) and the recorder persists only the stored
/// file NAME — not the owning user. A tenant-scoped lookup is therefore the only way to find the
/// file again from a <see cref="GroundingSample"/> alone.
/// </summary>
public interface IGroundingScreenshotLocator
{
    /// <summary>
    /// Absolute path of the screenshot for <paramref name="screenshotFileId"/> inside
    /// <paramref name="tenantId"/>'s upload root, or null when there is none / it is gone.
    /// Never leaves that tenant's root.
    /// </summary>
    string? Resolve(Guid tenantId, string? screenshotFileId);
}

/// <summary>
/// Default <see cref="IGroundingScreenshotLocator"/>: searches the per-user upload directories
/// under ONE tenant's upload root for the stored file name.
///
/// SECURITY: the id is collapsed with <see cref="Path.GetFileName(string)"/> before use, so a
/// traversal-shaped id ("../../etc/passwd") can only ever name a bare file, and the resolved
/// path is re-checked to be inside the tenant root. The tenant root itself is DERIVED from the
/// one authoritative mapping (<see cref="UserResourceAccessService.GetUploadDirectory"/>) rather
/// than re-spelled here, so a change to the upload layout cannot silently desynchronise the two.
/// </summary>
public sealed class UploadsGroundingScreenshotLocator : IGroundingScreenshotLocator
{
    private readonly Func<Guid, string> _tenantUploadRoot;

    public UploadsGroundingScreenshotLocator(UserResourceAccessService resources)
        : this(tenantId => TenantRootOf(resources, tenantId))
    {
    }

    /// <summary>Test seam: inject the tenant-root mapping directly (no process-wide current directory).</summary>
    internal UploadsGroundingScreenshotLocator(Func<Guid, string> tenantUploadRoot)
        => _tenantUploadRoot = tenantUploadRoot;

    /// <summary>
    /// The tenant's upload root = the parent of any user's upload directory under it. Derived
    /// from the authoritative mapping so the "Uploads/&lt;tenant&gt;/&lt;user&gt;" shape is
    /// spelled in exactly one place in the codebase.
    /// </summary>
    private static string TenantRootOf(UserResourceAccessService resources, Guid tenantId)
        => Path.GetDirectoryName(resources.GetUploadDirectory(tenantId, Guid.Empty))!;

    public string? Resolve(Guid tenantId, string? screenshotFileId)
    {
        if (string.IsNullOrWhiteSpace(screenshotFileId)) return null;

        var safeName = Path.GetFileName(screenshotFileId);
        if (string.IsNullOrWhiteSpace(safeName)) return null;

        string root;
        try
        {
            root = Path.GetFullPath(_tenantUploadRoot(tenantId));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!Directory.Exists(root)) return null;

        try
        {
            foreach (var userDirectory in Directory.EnumerateDirectories(root))
            {
                var candidate = Path.GetFullPath(Path.Combine(userDirectory, safeName));
                // Belt and braces: never hand back anything outside the tenant's own root.
                if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}
