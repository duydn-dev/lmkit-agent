using System.Text.Json;
using System.Text.Json.Serialization;
using LMKit.Agents.Tools;

namespace LmKitOmniApi.Infrastructure.AI.Tools;

/// <summary>
/// Tool "hỏi lại người dùng" với hợp đồng PHẲNG, không có JSON lồng trong chuỗi.
/// Model nhỏ (gemma/qwen 4B) thường escape sai khi phải nhúng một JSON array vào
/// tham số kiểu string — lớp function-calling của LM-Kit từ chối payload trước khi
/// code ứng dụng kịp chạy (lỗi "Invalid JSON arguments"), nên ask_clarification
/// gần như không bao giờ gọi được. Ở đây question + choices là text thuần:
/// choices ngăn cách bằng ';' hoặc xuống dòng, lựa chọn khuyến nghị đánh dấu '*'.
/// UI vẫn nhận đúng ClarificationOption {label,value,recommended} như cũ.
/// </summary>
public sealed class ClarificationTool : ITool
{
    public const string ToolName = "ask_clarification";

    private const string Schema = """
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "question": {
          "type": "string",
          "description": "The concise question to ask the user (1-1000 characters)."
        },
        "choices": {
          "type": "string",
          "description": "2 or 3 answer choices as PLAIN TEXT separated by ';' or newlines. Mark the recommended choice with a leading '*'. NEVER output JSON here. Example: '*Tong ket tai chinh; Danh gia KPI; Ke hoach quy toi'"
        }
      },
      "required": ["question", "choices"]
    }
    """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Func<string, string, CancellationToken, Task<string>> _invoke;

    public ClarificationTool(Func<string, string, CancellationToken, Task<string>> invoke)
        => _invoke = invoke;

    public string Name => ToolName;

    public string Description =>
        "Pause and ask the user one concise question ONLY when a missing fact materially changes the answer. " +
        "Supply 2-3 plain-text choices separated by ';' and prefix '*' on the recommended one. " +
        "The UI adds a free-text Other option. Never use for approval or ordinary uncertainty.";

    public string InputSchema => Schema;

    public async Task<string> InvokeAsync(string arguments, CancellationToken ct = default)
    {
        var (question, choices) = ParseArguments(arguments);
        var optionsJson = JsonSerializer.Serialize(BuildOptions(choices), JsonOptions);
        return await _invoke(question, optionsJson, ct);
    }

    private static (string Question, string Choices) ParseArguments(string arguments)
    {
        string? question = null;
        string? choices = null;
        try
        {
            var parsed = JsonSerializer.Deserialize<ClarificationArguments>(arguments, JsonOptions);
            question = parsed?.Question;
            choices = parsed?.Choices;
        }
        catch (JsonException)
        {
            // Fallback lenient — giống DelegatedActionTool: cứu từng field trước khi từ chối.
            question = ExtractStringField(arguments, "question");
            choices = ExtractStringField(arguments, "choices");
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException(
                $"Tool '{ToolName}' requires a non-empty 'question'.", nameof(arguments));
        }

        if (string.IsNullOrWhiteSpace(choices))
        {
            throw new ArgumentException(
                $"Tool '{ToolName}' requires 'choices': 2-3 options separated by ';' "
                + "(mark the recommended one with '*'). Plain text, never JSON.", nameof(arguments));
        }

        return (question.Trim(), choices);
    }

    /// <summary>
    /// Chuyển text "A; *B; C" thành đúng 2-3 ClarificationOption, luôn có đúng 1 recommended.
    /// Quy tắc "đúng một recommended" nằm ở ClarificationHeuristics.BuildOptions để đường dựng
    /// lại card từ văn xuôi dùng chung một luật với tool.
    /// </summary>
    private static List<ClarificationOption> BuildOptions(string choices)
    {
        var items = choices.Split([';', '\n', '\r'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();

        if (items.Count < 2)
        {
            throw new ArgumentException(
                $"Tool '{ToolName}' needs at least 2 choices separated by ';' (got: '{choices}').",
                nameof(choices));
        }

        return ClarificationHeuristics.BuildOptions(items, item => (item, item));
    }

    private static string? ExtractStringField(string? text, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var key = $"\"{fieldName}\"";
        var idx = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var colon = text.IndexOf(':', idx + key.Length);
        if (colon < 0) return null;

        var i = colon + 1;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length || text[i] != '"') return null;

        i++;
        var sb = new System.Text.StringBuilder();
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                switch (next)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    default: sb.Append(next); break;
                }
                i += 2;
                continue;
            }
            if (c == '"') return sb.ToString();
            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private sealed class ClarificationArguments
    {
        [JsonPropertyName("question")]
        public string? Question { get; init; }

        [JsonPropertyName("choices")]
        public string? Choices { get; init; }
    }
}
