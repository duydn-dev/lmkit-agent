using LmKitOmniApi.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LmKitOmniApi.Tests;

public class TokenManagementServiceTests
{
    private readonly TokenManagementService _service;

    public TokenManagementServiceTests()
    {
        _service = new TokenManagementService(null!, NullLogger<TokenManagementService>.Instance);
    }

    [Fact]
    public void EstimateTokenCount_NullOrEmpty_ReturnsZero()
    {
        Assert.Equal(0, _service.EstimateTokenCount(null!));
        Assert.Equal(0, _service.EstimateTokenCount(""));
    }

    [Fact]
    public void EstimateTokenCount_EnglishText_UsesEnglishRatio()
    {
        var text = "This is a simple English sentence without any special characters.";
        var expectedLength = text.Length;
        var expectedTokens = (int)System.Math.Ceiling(expectedLength / 4.0);
        
        var tokens = _service.EstimateTokenCount(text);
        
        Assert.Equal(expectedTokens, tokens);
    }

    [Fact]
    public void EstimateTokenCount_VietnameseText_UsesVietnameseRatio()
    {
        var text = "Xin chào, đây là một câu tiếng Việt với rất nhiều dấu câu và ký tự đặc biệt.";
        var expectedLength = text.Length;
        
        var tokens = _service.EstimateTokenCount(text);
        
        var englishTokens = (int)System.Math.Ceiling(expectedLength / 4.0);
        Assert.True(tokens > englishTokens);
    }
}
