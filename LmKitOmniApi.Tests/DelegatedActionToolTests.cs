using System.Text.Json;
using LmKitOmniApi.Infrastructure.AI.Tools;

namespace LmKitOmniApi.Tests;

public class DelegatedActionToolTests
{
    [Fact]
    public async Task InvokeAsync_UsesStructuredQuery()
    {
        string? received = null;
        var tool = new DelegatedActionTool(
            "test_tool",
            "Test tool",
            (query, _) =>
            {
                received = query;
                return Task.FromResult("ok");
            });

        var result = await tool.InvokeAsync("""{"query":"  hello  "}""");

        Assert.Equal("hello", received);
        Assert.Equal("ok", result);
        using var schema = JsonDocument.Parse(tool.InputSchema);
        Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task InvokeAsync_PassesOptionsJsonToStructuredCallback()
    {
        string? receivedQuery = null;
        string? receivedOptions = null;
        var tool = new DelegatedActionTool(
            "ask_clarification",
            "Ask a clarifying question",
            (query, optionsJson, _) =>
            {
                receivedQuery = query;
                receivedOptions = optionsJson;
                return Task.FromResult("ok");
            });

        await tool.InvokeAsync("""{"query":"Which format?","optionsJson":"[{\"label\":\"Brief\"}]"}""");

        Assert.Equal("Which format?", receivedQuery);
        Assert.Equal("[{\"label\":\"Brief\"}]", receivedOptions);
        using var schema = JsonDocument.Parse(tool.InputSchema);
        Assert.Contains("optionsJson", schema.RootElement.GetProperty("properties").EnumerateObject().Select(property => property.Name));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"query\":\"\"}")]
    [InlineData("not-json")]
    public async Task InvokeAsync_RejectsInvalidArguments(string arguments)
    {
        var tool = new DelegatedActionTool(
            "test_tool",
            "Test tool",
            (_, _) => Task.FromResult("should not run"));

        await Assert.ThrowsAsync<ArgumentException>(() => tool.InvokeAsync(arguments));
    }
}
