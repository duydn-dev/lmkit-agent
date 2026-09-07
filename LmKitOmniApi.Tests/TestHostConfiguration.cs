using Microsoft.AspNetCore.Hosting;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The ONE way an integration-test host sets configuration, and the reason there is
/// only one.
///
/// <para><b>The seam.</b> <c>WebApplicationFactory</c> relays the HOST configuration
/// layer into the entry point (as <c>--key=value</c> command-line arguments), so a value
/// written with <see cref="IWebHostBuilder.UseSetting"/> is already present in
/// <c>builder.Configuration</c> when the FIRST line of <c>Program.cs</c>'s top-level
/// statements runs. A <c>ConfigureAppConfiguration</c> layer is merged during
/// <c>builder.Build()</c> instead — <c>Program.cs</c> line 707, which is roughly 670
/// lines after the top-level code has read the LM-Kit licence key, the data-protection
/// paths, the Postgres and Redis connection strings, the OTLP endpoint, the
/// forwarded-header trust list and the AI rate-limit budget into local variables. An
/// override of any of those made through <c>ConfigureAppConfiguration</c> was silently
/// INERT: nothing failed, the host simply kept the <c>appsettings.json</c> value and the
/// test measured the shipped default.</para>
///
/// <para><b>Why not both.</b> Writing every setting to both layers looks safer and is
/// worse. Measured on this solution (<c>UseSetting</c> vs <c>ConfigureAppConfiguration</c>
/// for the same key, read back from the running host's <c>IConfiguration</c>): the app
/// layer WINS post-build, because its provider is appended after the command-line one.
/// A host would then answer differently depending on WHEN a setting is read — the
/// rate-limit policy built before <c>Build()</c> seeing one number and
/// <c>DistributedAiRateLimitMiddleware</c>, constructed per request, seeing another. That
/// is the same class of lie the seam was fixed to remove, and it bites hardest exactly
/// where a derived host (<see cref="RateLimitTestHost"/>) tries to override a value its
/// parent factory has already set. So the app-configuration layer is not used at all:
/// every test-host setting is host configuration, one layer, one precedence rule
/// (last writer wins), visible everywhere.</para>
/// </summary>
internal static class TestHostConfiguration
{
    /// <summary>
    /// Settings shared by every API test host: a usable JWT signing key, cookies that a
    /// plain <c>HttpClient</c> can carry, no HTTPS redirection, no migrations, no
    /// bootstrap admin, no Redis, and no model warm-up.
    /// </summary>
    public static Dictionary<string, string?> SharedDefaults() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["JwtSettings:SecretKey"] = "integration-test-secret-key-at-least-32-bytes-long",
        ["JwtSettings:Issuer"] = "LmKitOmniApi",
        ["JwtSettings:Audience"] = "LmKitOmniClient",
        ["JwtSettings:ExpirationInMinutes"] = "30",
        ["AuthCookies:Secure"] = "false",
        ["HttpsRedirection:Enabled"] = "false",
        ["Database:ApplyMigrations"] = "false",
        ["BootstrapAdmin:Enabled"] = "false",
        ["ConnectionStrings:Redis"] = "",
        ["AiModels:WarmupChatModel"] = "false",
        ["AiModels:RequireChatModelReady"] = "false"
    };

    /// <summary>The one setting that must never be shared between two live hosts.</summary>
    public const string DataProtectionKeyPathSetting = "DataProtection:KeyPath";

    /// <summary>
    /// Mints a data-protection key-ring directory that no other host will ever open.
    ///
    /// <para><b>Why per host.</b> <c>Program.cs</c> reads
    /// <see cref="DataProtectionKeyPathSetting"/> in a top-level statement and defaults it
    /// to <c>&lt;content root&gt;/App_Data/DataProtectionKeys</c> — ONE directory shared by
    /// every factory in the process. Under real parallelism several hosts create and read
    /// that key ring at the same time, and a host that reads a half-written key XML fails
    /// its first protected operation, which surfaces as a 500 on login and takes the whole
    /// fixture with it. Reproduced at <c>xUnit.MaxParallelThreads=32</c>.</para>
    ///
    /// <para><b>Why a DERIVED host needs its own.</b> <c>WithWebHostBuilder</c> replays the
    /// parent's <c>ConfigureWebHost</c> — including the parent's key path — and then layers
    /// the derived overrides on top. A derived host that does not override this setting
    /// therefore runs a SECOND host on the parent's key ring, which is precisely the
    /// configuration the per-host path was introduced to eliminate; the parent factory is a
    /// long-lived class/collection fixture, so the two are live at the same time by
    /// construction.</para>
    ///
    /// <para>Passing <paramref name="parentKeyPath"/> nests the new ring INSIDE the parent's
    /// directory. That is deliberate and does two things at once: the directories are
    /// distinct (<c>FileSystemXmlRepository</c> enumerates <c>*.xml</c> in its own directory
    /// only, never a subdirectory, so neither host can see the other's keys), and the
    /// parent's <c>Dispose</c> — which deletes its directory recursively — also cleans up
    /// every ring derived from it, so no derived host leaks a temp directory.</para>
    /// </summary>
    public static string NewDataProtectionKeyPath(string? parentKeyPath = null)
        => Path.Combine(
            string.IsNullOrWhiteSpace(parentKeyPath) ? Path.GetTempPath() : parentKeyPath,
            $"lmkit-tests-dpkeys-{Guid.NewGuid():N}");

    /// <summary>
    /// Host configuration for a host DERIVED from <paramref name="parentKeyPath"/>'s factory
    /// via <c>WithWebHostBuilder</c>. Identical to <see cref="Apply"/> except that it also
    /// gives the derived host its own key ring unless the caller deliberately named one —
    /// the single thing a derived host must not inherit. Call it instead of
    /// <see cref="Apply"/> from EVERY <c>WithWebHostBuilder</c> callback;
    /// <c>DerivedTestHostIsolationTests</c> is what keeps that true.
    /// </summary>
    public static void ApplyDerived(
        IWebHostBuilder builder,
        string parentKeyPath,
        IEnumerable<KeyValuePair<string, string?>> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings) merged[key] = value;
        if (!merged.ContainsKey(DataProtectionKeyPathSetting))
            merged[DataProtectionKeyPathSetting] = NewDataProtectionKeyPath(parentKeyPath);

        Apply(builder, merged);
    }

    /// <summary>
    /// Writes <paramref name="settings"/> as host configuration. Later keys overwrite
    /// earlier ones, including across calls, so a factory can apply its defaults first
    /// and a per-test override afterwards.
    /// </summary>
    /// <remarks>
    /// A <see langword="null"/> value is REFUSED rather than relayed. The host layer
    /// travels as <c>--key=value</c>, where a null is indistinguishable from the empty
    /// string; accepting it would reintroduce exactly the silent mismatch this type
    /// exists to prevent (an in-memory null makes <c>GetValue&lt;int&gt;</c> fall back to
    /// its default, an empty string makes it throw). Tests that mean "empty" say so.
    /// </remarks>
    public static void Apply(IWebHostBuilder builder, IEnumerable<KeyValuePair<string, string?>> settings)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(settings);

        foreach (var (key, value) in settings)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("A test host setting must have a non-empty key.");
            if (value is null)
                throw new InvalidOperationException(
                    $"Test host setting '{key}' was given a null value. Host configuration is relayed to "
                    + "the entry point as --key=value, which cannot express null; use string.Empty when "
                    + "the setting is meant to be blank.");

            builder.UseSetting(key, value);
        }
    }
}

/// <summary>
/// Extra configuration layered on top of a factory's shared defaults, for a one-off host
/// (a tighter rate-limit window, a feature flag flipped on). Values are applied as host
/// configuration by <see cref="TestHostConfiguration.Apply"/> and therefore reach both
/// <c>Program.cs</c>'s top-level statements and anything that resolves
/// <c>IConfiguration</c> later.
///
/// <para>Populate it BEFORE touching <c>Services</c>/<c>CreateClient</c>: the host is
/// built lazily on first use and its configuration is fixed at that point. A write made
/// afterwards used to be a silent no-op — it now throws, because an override that does
/// nothing is worse than one that is unsupported.</para>
/// </summary>
public sealed class TestHostConfigurationOverrides
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    private bool _applied;

    public string? this[string key]
    {
        get => _values.TryGetValue(key, out var value) ? value : null;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (_applied)
                throw new InvalidOperationException(
                    $"Configuration override '{key}' was set after the test host had already been built, "
                    + "so it could not take effect. Populate ConfigurationOverrides before the first use of "
                    + "Services/CreateClient/EnsureSeeded, or build a dedicated factory for this test.");

            _values[key] = value;
        }
    }

    /// <summary>Marks the overrides as consumed and returns them for application.</summary>
    internal IReadOnlyDictionary<string, string?> Consume()
    {
        _applied = true;
        return _values;
    }
}
