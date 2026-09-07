using LmKitOmniApi.Infrastructure.AI.Tools;

namespace LmKitOmniApi.Tests;

public class LmKitDefaultToolCatalogTests
{
    private readonly LmKitDefaultToolCatalog _catalog = new();

    [Fact]
    public void SafeDefaults_ContainOnlyCuratedLowRiskTools()
    {
        var described = _catalog.DescribeTools().Select(tool => tool.Name).ToArray();

        Assert.Equal(
            new[]
            {
                "calc_arithmetic",
                "datetime_now",
                "json_parse",
                "csv_parse",
                "xml_parse",
                "stats_analysis",
            },
            described);
    }

    /// <summary>
    /// The regression this file exists for: the catalog used to advertise 24 tools
    /// while registering 6. Descriptors and registrations now come from one table,
    /// so the two can only ever agree — in count, in order, and name for name.
    /// </summary>
    [Fact]
    public void EveryDescribedTool_IsActuallyRegistered_UnderTheNameItAdvertises()
    {
        var described = _catalog.DescribeTools();
        var registered = _catalog.GetSafeDefaultTools();

        Assert.Equal(described.Count, registered.Count);
        Assert.Equal(
            described.Select(tool => tool.Name),
            registered.Select(tool => tool.Name));
    }

    [Fact]
    public void SideEffectingOrResourceTools_AreNeverEnabledByDefault()
    {
        // Every described tool IS an enabled tool now, so this covers the whole
        // catalog: nothing that touches the filesystem, the network or a document
        // may be registered here, because that path skips the permission gateway
        // the application's own action dispatcher enforces.
        var unsafeDefaults = _catalog.DescribeTools()
            .Where(tool =>
                tool.Category is "IO" or "Net" or "Document"
                || tool.Name.Contains("write", StringComparison.OrdinalIgnoreCase)
                || tool.Name.Contains("delete", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(unsafeDefaults);
    }

    [Fact]
    public void EveryDescriptor_StatesWhyItIsSafe()
    {
        Assert.All(
            _catalog.DescribeTools(),
            tool =>
            {
                Assert.False(string.IsNullOrWhiteSpace(tool.Category));
                Assert.False(string.IsNullOrWhiteSpace(tool.Rationale));
            });
    }

    [Fact]
    public void CatalogNames_AreUnique()
    {
        var names = _catalog.DescribeTools().Select(tool => tool.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// LM-Kit's BuiltInTools properties document themselves as returning a NEW
    /// instance per access, and the runtime builds one conversation per request:
    /// two calls must never hand the same tool object to two conversations.
    /// </summary>
    [Fact]
    public void GetSafeDefaultTools_ReturnsFreshInstancesPerCall()
    {
        var first = _catalog.GetSafeDefaultTools();
        var second = _catalog.GetSafeDefaultTools();

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
            Assert.NotSame(first[i], second[i]);
    }
}
