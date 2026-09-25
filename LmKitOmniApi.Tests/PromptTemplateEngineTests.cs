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
        // Fallback vẫn là template "default" — nhận diện qua câu nhiệm vụ, không qua
        // tên một đơn vị cụ thể (đa tenant: không đơn vị nào được hardcode).
        Assert.Contains("Nhiệm vụ của bạn", result);
        Assert.DoesNotContain("Trung tâm thông tin lưu trữ", result);
    }

    [Fact]
    public void Render_DefaultTemplate_IncludesOrganizationName_WhenProvided()
    {
        var result = _engine.Render("default", new Dictionary<string, string>
        {
            ["agent_name"] = "Trợ lý HTQT",
            ["org_name"] = "Vụ hợp tác quốc tế"
        });
        Assert.Contains("Trợ lý HTQT", result);
        Assert.Contains("Vụ hợp tác quốc tế", result);
    }

    [Fact]
    public void Render_DefaultTemplate_OmitsOrganizationClause_WhenAbsent()
    {
        var result = _engine.Render("default", new Dictionary<string, string>
        {
            ["agent_name"] = "Trợ lý ảo",
            ["org_name"] = ""
        });
        Assert.DoesNotContain("trợ lý ảo của", result);
        Assert.DoesNotContain("Trung tâm", result);
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
