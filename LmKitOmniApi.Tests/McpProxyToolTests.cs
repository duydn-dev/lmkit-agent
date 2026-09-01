using System.Text.Json;
using LmKitOmniApi.Infrastructure.AI.Mcp;
using LmKitOmniApi.Infrastructure.AI.Tools;

namespace LmKitOmniApi.Tests;

public class McpProxyToolTests
{
    [Fact]
    public async Task BuildsStrictSchemaAndForwardsTypedArguments()
    {
        IReadOnlyDictionary<string, object>? captured = null;
        var definition = new McpToolDefinition
        {
            ServerName = "Issue Tracker",
            Name = "create-issue",
            Description = "Create an issue",
            Parameters =
            [
                new() { Name = "title", Type = "string", Required = true },
                new() { Name = "priority", Type = "integer" },
            ]
        };

        var tool = new McpProxyTool(definition, (parameters, _) =>
        {
            captured = parameters;
            return Task.FromResult("created");
        });

        var result = await tool.InvokeAsync("""{"title":"Bug","priority":2}""");

        Assert.StartsWith("mcp_issue_tracker_create_issue_", tool.Name);
        Assert.True(tool.Name.Length <= 64);
        Assert.Equal("created", result);
        Assert.Equal("Bug", captured!["title"]);
        Assert.Equal(2L, captured["priority"]);
        using var schema = JsonDocument.Parse(tool.InputSchema);
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("title", schema.RootElement.GetProperty("required")[0].GetString());
    }

    [Fact]
    public async Task PreservesServerSchemaAndNestedArguments()
    {
        IReadOnlyDictionary<string, object>? captured = null;
        const string inputSchema = """
            {"type":"object","properties":{"filters":{"type":"object","properties":{"labels":{"type":"array","items":{"type":"string"}}}}},"required":["filters"],"additionalProperties":false}
            """;
        var tool = new McpProxyTool(new McpToolDefinition
        {
            ServerName = "issues",
            Name = "search",
            InputSchema = inputSchema
        }, (parameters, _) =>
        {
            captured = parameters;
            return Task.FromResult("ok");
        });

        await tool.InvokeAsync("""{"filters":{"labels":["security","ai"]}}""");

        Assert.True(JsonElement.DeepEquals(
            JsonDocument.Parse(inputSchema).RootElement,
            JsonDocument.Parse(tool.InputSchema).RootElement));
        var filters = Assert.IsType<JsonElement>(captured!["filters"]);
        Assert.Equal("security", filters.GetProperty("labels")[0].GetString());
    }
}
