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
