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
        }
      },
      "required": ["query"]
    }
    """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Func<string, CancellationToken, Task<string>> _invoke;

    public DelegatedActionTool(
        string name,
        string description,
        Func<string, CancellationToken, Task<string>> invoke)
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
        catch (JsonException ex)
        {
            throw new ArgumentException($"Invalid JSON arguments for tool '{Name}'.", nameof(arguments), ex);
        }

        if (string.IsNullOrWhiteSpace(parsed?.Query))
        {
            throw new ArgumentException($"Tool '{Name}' requires a non-empty query.", nameof(arguments));
        }

        return _invoke(parsed.Query.Trim(), ct);
    }

    private sealed class DelegatedActionArguments
    {
        [JsonPropertyName("query")]
        public string? Query { get; init; }
    }
}
