using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Infrastructure.AI.Filters;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

public class OutputGuardrailFilterTests
{
    [Fact]
    public async Task SensitiveOutput_IsRedactedBeforeDelivery()
    {
        var guard = new PromptGuardService(NullLogger<PromptGuardService>.Instance);
        var filter = new OutputGuardrailFilter(guard, NullLogger<OutputGuardrailFilter>.Instance);
        var context = new AgentFilterContext
        {
            Output = "Email private@example.com and API_KEY=super-secret-value"
        };

        var result = await filter.OnOutputAsync(context);

        Assert.DoesNotContain("private@example.com", result.ProcessedContent);
        Assert.DoesNotContain("super-secret-value", result.ProcessedContent);
        Assert.Contains("[EMAIL REDACTED]", result.ProcessedContent);
        Assert.Contains("[REDACTED]", result.ProcessedContent);
    }
}
