using System.Collections.Concurrent;

namespace LmKitOmniApi.Infrastructure.AI;

/// <summary>
/// Prompt Template Engine — replaces hardcoded system prompts with
/// configurable, context-aware templates. Supports variable substitution.
/// Addresses gap: "agent-templates — Không có Prompt Template Engine"
/// </summary>
public class PromptTemplateEngine
{
    // H4 Fix: Use ConcurrentDictionary for thread-safe access.
    // PromptTemplateEngine is registered as Singleton — concurrent access from multiple requests.
    private static readonly ConcurrentDictionary<string, string> Templates = new()
    {
        ["default"] = @"Bạn là {{agent_name}}, một trợ lý AI thông minh, nhiệt tình và thân thiện.
Bạn phải luôn luôn giao tiếp bằng tiếng Việt chuẩn xác, tự nhiên và trôi chảy.
Trình bày câu trả lời rõ ràng, súc tích và tránh sử dụng các ký tự lỗi hoặc từ ngữ không hợp lệ.
{{#if context}}

Dữ liệu tham khảo:
{{context}}
{{/if}}
{{#if memory}}

Ký ức từ các cuộc trò chuyện trước:
{{memory}}
{{/if}}
{{#if skills}}

{{skills}}
{{/if}}",

        ["research"] = @"Bạn là {{agent_name}}, chuyên gia nghiên cứu và phân tích.
Nhiệm vụ: tìm kiếm thông tin chính xác từ nhiều nguồn và tổng hợp câu trả lời.
Luôn trích dẫn nguồn khi có thể.
{{#if context}}

Dữ liệu tham khảo:
{{context}}
{{/if}}",

        ["analysis"] = @"Bạn là {{agent_name}}, chuyên gia phân tích dữ liệu và văn bản.
Nhiệm vụ: phân tích sâu, đưa ra nhận định có căn cứ, trình bày cấu trúc rõ ràng.
{{#if context}}

Dữ liệu phân tích:
{{context}}
{{/if}}",

        ["vision"] = @"Bạn là {{agent_name}}, chuyên gia xử lý hình ảnh và OCR.
Nhiệm vụ: mô tả chính xác nội dung hình ảnh, trích xuất văn bản, nhận dạng đối tượng.
{{#if context}}

Kết quả OCR/Vision:
{{context}}
{{/if}}",

        ["reasoning"] = @"You are a reasoning agent. Based on the user's query and any existing context,
decide the NEXT action to take.

{{skills}}

Rules:
- Choose ONLY ONE action per turn
- If context already has enough information, choose DONE
- Output ONLY the action name (e.g., 'RAG' or 'DONE')
- After iteration {{max_iterations}}, always choose DONE
- For complex research tasks, prefer DELEGATE
- For external tool needs, prefer MCP",

        ["summarize"] = @"Bạn là {{agent_name}}, chuyên gia tóm tắt.
Hãy tóm tắt nội dung sau một cách súc tích, giữ lại các ý chính quan trọng.
Trình bày dưới dạng bullet points.

Nội dung cần tóm tắt:
{{context}}"
    };

    /// <summary>
    /// Render a prompt template with variable substitution.
    /// Supports {{variable}} and {{#if variable}}...{{/if}} blocks.
    /// </summary>
    public string Render(string templateName, Dictionary<string, string> variables)
    {
        if (!Templates.TryGetValue(templateName, out var template))
            template = Templates["default"];

        // Process conditional blocks: {{#if var}}content{{/if}}
        template = ProcessConditionals(template, variables);

        // Replace simple variables: {{var}}
        foreach (var kvp in variables)
        {
            template = template.Replace($"{{{{{kvp.Key}}}}}", kvp.Value);
        }

        // Clean up any unreplaced variables
        template = System.Text.RegularExpressions.Regex.Replace(
            template, @"\{\{[a-z_]+\}\}", "");

        return template.Trim();
    }

    /// <summary>
    /// Get available template names.
    /// </summary>
    public IReadOnlyList<string> GetTemplateNames() => Templates.Keys.ToList();

    /// <summary>
    /// Register a custom template at runtime.
    /// </summary>
    public void RegisterTemplate(string name, string template)
    {
        Templates[name] = template;
    }

    private static string ProcessConditionals(string template, Dictionary<string, string> variables)
    {
        // Pattern: {{#if varname}}content{{/if}}
        var pattern = @"\{\{#if\s+(\w+)\}\}(.*?)\{\{/if\}\}";
        return System.Text.RegularExpressions.Regex.Replace(
            template,
            pattern,
            match =>
            {
                var varName = match.Groups[1].Value;
                var content = match.Groups[2].Value;
                // Include content only if variable exists and is non-empty
                if (variables.TryGetValue(varName, out var value) && !string.IsNullOrWhiteSpace(value))
                    return content;
                return "";
            },
            System.Text.RegularExpressions.RegexOptions.Singleline);
    }
}
