using LmKitOmniApi.Infrastructure.AI;
using Xunit;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace LmKitOmniApi.Tests;

public class PromptTemplateEngineTests
{
    private readonly PromptTemplateEngine _engine;

    public PromptTemplateEngineTests()
    {
        _engine = new PromptTemplateEngine();
    }

    [Fact]
    public void Render_UnknownTemplate_ReturnsFallback()
    {
        var result = _engine.Render("unknown_template", new Dictionary<string, string>());
        Assert.Contains("trợ lý AI", result); // Checks the default fallback
    }

    [Fact]
    public async Task RegisterTemplate_ThreadSafety_WorksUnderConcurrency()
    {
        // Act
        var tasks = new Task[100];
        for (int i = 0; i < 100; i++)
        {
            var index = i;
            tasks[i] = Task.Run(() => 
            {
                _engine.RegisterTemplate($"template_{index}", $"Content {index}");
            });
        }
        await Task.WhenAll(tasks);

        // Assert
        for (int i = 0; i < 100; i++)
        {
            var result = _engine.Render($"template_{i}", new Dictionary<string, string>());
            Assert.Contains($"Content {i}", result);
        }
    }
}
