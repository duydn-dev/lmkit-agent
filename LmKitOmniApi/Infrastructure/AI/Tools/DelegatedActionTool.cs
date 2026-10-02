using System.Text.Json;
using System.Text.Json.Serialization;
using LMKit.Agents.Tools;

namespace LmKitOmniApi.Infrastructure.AI.Tools;

/// <summary>
/// Adapts an application action to LM-Kit.NET's native structured tool contract.
/// Security, approval and sandbox behavior remain in the application callback;
/// this type is only the JSON-schema/function-calling boundary.
/// </summary>
public sealed class DelegatedActionTool : ITool
{
    private const string Schema = """
    {
      "type": "object",
      "additionalProperties": false,
      "properties": {
        "query": {
          "type": "string",
          "description": "The complete request or resource path needed by the tool."
        },
        "optionsJson": {
          "type": "string",
          "description": "For ask_clarification only: a JSON array of 2-3 {label,value,recommended} choices, with exactly one recommended."
        }
      },
      "required": ["query"]
    }
    """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Func<string, string?, CancellationToken, Task<string>> _invoke;

    public DelegatedActionTool(
        string name,
        string description,
        Func<string, CancellationToken, Task<string>> invoke)
        : this(name, description, (query, _, ct) => invoke(query, ct))
    {
    }

    public DelegatedActionTool(
        string name,
        string description,
        Func<string, string?, CancellationToken, Task<string>> invoke)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(invoke);

        Name = name;
        Description = description;
        _invoke = invoke;
    }

    public string Name { get; }
    public string Description { get; }
    public string InputSchema => Schema;

    public Task<string> InvokeAsync(string arguments, CancellationToken ct = default)
    {
        DelegatedActionArguments? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<DelegatedActionArguments>(arguments, JsonOptions);
        }
        catch (JsonException)
        {
            // Small models thường escape sai khi nhúng JSON array vào chuỗi optionsJson
            // (ví dụ "optionsJson": "[{"label":...}]") → toàn bộ tool call trở thành JSON
            // không hợp lệ. Cứu lấy query/optionsJson bằng parse lenient trước khi từ chối,
            // nếu không ask_clarification gần như không bao giờ gọi được từ model nhỏ.
            parsed = TryRecoverArguments(arguments);
            if (parsed is null)
            {
                throw new ArgumentException($"Invalid JSON arguments for tool '{Name}'.", nameof(arguments));
            }
        }

        if (string.IsNullOrWhiteSpace(parsed?.Query))
        {
            throw new ArgumentException($"Tool '{Name}' requires a non-empty query.", nameof(arguments));
        }

        return _invoke(parsed.Query.Trim(), parsed.OptionsJson, ct);
    }

    private sealed class DelegatedActionArguments
    {
        [JsonPropertyName("query")]
        public string? Query { get; init; }

        [JsonPropertyName("optionsJson")]
        public string? OptionsJson { get; init; }
    }

    /// <summary>
    /// Lenient fallback: trích "query" và mảng JSON của "optionsJson" từ tool-call arguments
    /// mà strict parser từ chối (code fences, dấu phẩy thừa, quote không escape...).
    /// Chỉ chạy khi JsonSerializer fail nên không đổi hành vi của call hợp lệ.
    /// </summary>
    private static DelegatedActionArguments? TryRecoverArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return null;

        var text = arguments.Trim();
        // Bỏ code fence ```json ... ``` mà model hay bọc quanh tool call.
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                text = text[(firstNewline + 1)..lastFence].Trim();
            }
        }

        var query = ExtractStringField(text, "query");
        if (string.IsNullOrWhiteSpace(query)) return null;

        var optionsJson = ExtractStringField(text, "optionsJson");
        if (optionsJson is null)
        {
            // optionsJson có thể là mảng KHÔNG bọc trong chuỗi: [{"label":"A",...}].
            var arrayStart = text.IndexOf("optionsJson", StringComparison.OrdinalIgnoreCase);
            if (arrayStart >= 0)
            {
                var bracket = text.IndexOf('[', arrayStart);
                if (bracket >= 0)
                {
                    var end = FindMatchingBracket(text, bracket);
                    if (end > bracket)
                    {
                        optionsJson = text[bracket..(end + 1)];
                    }
                }
            }
        }

        return new DelegatedActionArguments { Query = query, OptionsJson = optionsJson };
    }

    private static string? ExtractStringField(string text, string fieldName)
    {
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

    private static int FindMatchingBracket(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '[') depth++;
            else if (c == ']')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
    }
}
