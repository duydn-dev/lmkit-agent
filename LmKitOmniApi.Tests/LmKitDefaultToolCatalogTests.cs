using LmKitOmniApi.Infrastructure.AI.Tools;

namespace LmKitOmniApi.Tests;

public class LmKitDefaultToolCatalogTests
{
    private readonly LmKitDefaultToolCatalog _catalog = new();

    [Fact]
    public void SafeDefaults_ContainOnlyCuratedLowRiskTools()
    {
        var enabled = _catalog.DescribeTools()
            .Where(tool => tool.Activation == ToolActivation.Default)
            .Select(tool => tool.Name)
            .ToArray();

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
            enabled);

        Assert.Equal(enabled.Length, _catalog.GetSafeDefaultTools().Count);
        Assert.Equal(
            enabled.Order(StringComparer.Ordinal),
            _catalog.GetSafeDefaultTools()
                .Select(tool => tool.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SideEffectingOrResourceTools_AreNeverEnabledByDefault()
    {
        var unsafeDefaults = _catalog.DescribeTools()
            .Where(tool => tool.Activation == ToolActivation.Default)
            .Where(tool =>
                tool.Category is "IO" or "Net" or "Document"
                || tool.Name.Contains("write", StringComparison.OrdinalIgnoreCase)
                || tool.Name.Contains("delete", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(unsafeDefaults);
    }

    [Fact]
    public void CatalogNames_AreUnique()
    {
        var names = _catalog.DescribeTools().Select(tool => tool.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
