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

        Assert.Equal("mcp_issue_tracker_create_issue", tool.Name);
        Assert.Equal("created", result);
        Assert.Equal("Bug", captured!["title"]);
        Assert.Equal(2L, captured["priority"]);
        using var schema = JsonDocument.Parse(tool.InputSchema);
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("title", schema.RootElement.GetProperty("required")[0].GetString());
    }
}
