using LmKitOmniApi.Infrastructure.AI.Documents;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Resolve đường dẫn license theo ContentRootPath (IHostEnvironment): thứ tự ưu
/// tiên cấu hình tường minh → thư mục quy ước &lt;ContentRoot&gt;/Aspose (*.lic
/// trước, rồi "License Key.txt"), và null khi không có gì.
/// </summary>
public sealed class AsposeLicenseResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lmkit-lic-tests", Guid.NewGuid().ToString("N"));

    public AsposeLicenseResolutionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private OfficeAuthoringService Build(OfficeAuthoringOptions? options = null)
        => new(
            new UserResourceAccessService(new ToolSandboxService(NullLogger<ToolSandboxService>.Instance)),
            Options.Create(options ?? new OfficeAuthoringOptions()),
            new TestHostEnvironment(_root),
            NullLogger<OfficeAuthoringService>.Instance);

    private string WriteAsposeFile(string name)
    {
        var dir = Path.Combine(_root, "Aspose");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "<License/>");
        return path;
    }

    [Fact]
    public void NoAsposeFolder_ResolvesToNull()
        => Assert.Null(Build().ResolvedLicensePath);

    [Fact]
    public void AsposeFolderWithLicFile_IsPickedUp()
    {
        var expected = WriteAsposeFile("license.lic");
        Assert.Equal(expected, Build().ResolvedLicensePath);
    }

    [Fact]
    public void LicenseKeyTxt_IsPickedUp_WhenNoLicFile()
    {
        var expected = WriteAsposeFile("License Key.txt");
        Assert.Equal(expected, Build().ResolvedLicensePath);
    }

    [Fact]
    public void LicFile_TakesPrecedence_OverLicenseKeyTxt()
    {
        var lic = WriteAsposeFile("license.lic");
        WriteAsposeFile("License Key.txt");
        Assert.Equal(lic, Build().ResolvedLicensePath);
    }

    [Fact]
    public void ExplicitRelativePath_ResolvesAgainstContentRoot_AndBeatsTheFolder()
    {
        WriteAsposeFile("license.lic"); // có sẵn trong thư mục quy ước…
        var custom = Path.Combine(_root, "secrets");
        Directory.CreateDirectory(custom);
        var customFile = Path.Combine(custom, "aspose-total.lic");
        File.WriteAllText(customFile, "<License/>");

        // …nhưng cấu hình tường minh (tương đối) thắng, resolve theo ContentRoot.
        var resolved = Build(new OfficeAuthoringOptions { AsposeLicensePath = "secrets/aspose-total.lic" })
            .ResolvedLicensePath;
        // So khớp theo đường dẫn chuẩn hoá (Path.Combine giữ nguyên "/" của cấu hình).
        Assert.Equal(Path.GetFullPath(customFile), Path.GetFullPath(resolved!));
    }

    [Fact]
    public void ExplicitAbsolutePath_IsUsedVerbatim()
    {
        var abs = Path.Combine(_root, "elsewhere.lic");
        File.WriteAllText(abs, "<License/>");
        Assert.Equal(abs, Build(new OfficeAuthoringOptions { AsposeLicensePath = abs }).ResolvedLicensePath);
    }

    [Fact]
    public void ExplicitPathMissing_FallsBackToTheAsposeFolder()
    {
        var folderLic = WriteAsposeFile("license.lic");
        var resolved = Build(new OfficeAuthoringOptions { AsposeLicensePath = "khong-ton-tai.lic" })
            .ResolvedLicensePath;
        Assert.Equal(folderLic, resolved); // cấu hình trỏ file vắng → quay về thư mục quy ước
    }
}
