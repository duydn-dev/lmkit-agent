using LmKitOmniApi.Infrastructure.Data.Interceptors;

namespace LmKitOmniApi.Tests;

public class AuditValueSanitizerTests
{
    [Theory]
    [InlineData("PasswordHash")]
    [InlineData("RefreshToken")]
    [InlineData("ApiSecret")]
    [InlineData("PrivateKeyPem")]
    [InlineData("MemoryValue")]
    [InlineData("ParametersJson")]
    [InlineData("Email")]
    public void SensitiveValues_AreRedacted(string propertyName) =>
        Assert.Equal("[REDACTED]", AuditValueSanitizer.Sanitize(propertyName, "classified"));

    [Fact]
    public void LongOrdinaryValue_IsBounded()
    {
        var result = Assert.IsType<string>(AuditValueSanitizer.Sanitize("Description", new string('a', 800)));
        Assert.True(result.Length < 550);
        Assert.EndsWith("[TRUNCATED]", result);
    }
}
