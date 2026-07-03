

using Jint;
using Jint.Constraints;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Security;

public interface IExecutionSandboxEngine
{
    Task<string> ExecuteCodeSafelyAsync(string codeSnippet, string language, CancellationToken ct = default);
}

public class ExecutionSandboxEngine : IExecutionSandboxEngine
{
    private readonly ILogger<ExecutionSandboxEngine> _logger;

    public ExecutionSandboxEngine(ILogger<ExecutionSandboxEngine> logger)
    {
        _logger = logger;
    }

    public async Task<string> ExecuteCodeSafelyAsync(string codeSnippet, string language, CancellationToken ct = default)
    {
        if (language.ToLowerInvariant() != "javascript" && language.ToLowerInvariant() != "js")
        {
            return "[Sandbox Error] Chỉ hỗ trợ thực thi an toàn ngôn ngữ JavaScript (qua Jint).";
        }

        try
        {
            _logger.LogInformation("🚀 Bắt đầu thực thi JavaScript trong Sandbox...");
            
            // Thiết lập Sandbox an toàn tuyệt đối
            var engine = new Engine(options => {
                options.LimitMemory(4 * 1024 * 1024); // Giới hạn 4MB RAM
                options.TimeoutInterval(TimeSpan.FromSeconds(2)); // Quá 2s là kill
                options.MaxStatements(10000); // Tránh vòng lặp vô tận
            });

            // Chạy script
            var result = engine.Evaluate(codeSnippet);
            
            _logger.LogInformation("✅ Sandbox thực thi thành công.");
            return await Task.FromResult(result.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning("❌ Lỗi Sandbox: {Error}", ex.Message);
            return $"[Sandbox Error] {ex.Message}";
        }
    }
}
