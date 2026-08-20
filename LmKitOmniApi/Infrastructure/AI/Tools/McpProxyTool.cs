using System.Text.Json;
using System.Text.RegularExpressions;
using LMKit.Agents.Tools;
using LmKitOmniApi.Infrastructure.AI.Mcp;

namespace LmKitOmniApi.Infrastructure.AI.Tools;

/// <summary>Exposes one discovered MCP capability as one structured LM-Kit tool.</summary>
public sealed class McpProxyTool : ITool
{
    private readonly Func<IReadOnlyDictionary<string, object>, CancellationToken, Task<string>> _invoke;

    public McpProxyTool(
        McpToolDefinition definition,
        Func<IReadOnlyDictionary<string, object>, CancellationToken, Task<string>> invoke)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(invoke);

        OriginalName = definition.Name;
        Name = NormalizeName($"mcp_{definition.ServerName}_{definition.Name}");
        Description = string.IsNullOrWhiteSpace(definition.Description)
            ? $"Invoke MCP tool '{definition.Name}'."
            : definition.Description;
        InputSchema = BuildSchema(definition.Parameters);
        _invoke = invoke;
    }

    public string OriginalName { get; }
    public string Name { get; }
    public string Description { get; }
    public string InputSchema { get; }

    public async Task<string> InvokeAsync(string arguments, CancellationToken ct = default)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(arguments);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Invalid JSON arguments for MCP tool '{OriginalName}'.", nameof(arguments), ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("MCP tool arguments must be a JSON object.", nameof(arguments));

            var parameters = document.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => ConvertValue(property.Value));
            return await _invoke(parameters, ct);
        }
    }

    private static string NormalizeName(string value)
    {
        var normalized = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9_]+", "_").Trim('_');
        return normalized.Length <= 64 ? normalized : normalized[..64];
    }

    private static string BuildSchema(IReadOnlyList<McpToolParameter> parameters)
    {
        var properties = parameters.ToDictionary(
            parameter => parameter.Name,
            parameter => (object)new Dictionary<string, object>
            {
                ["type"] = NormalizeJsonType(parameter.Type),
                ["description"] = parameter.Description ?? string.Empty,
            });

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = properties,
        };

        var required = parameters.Where(parameter => parameter.Required).Select(parameter => parameter.Name).ToArray();
        if (required.Length > 0) schema["required"] = required;
        return JsonSerializer.Serialize(schema);
    }

    private static string NormalizeJsonType(string type) => type.Trim().ToLowerInvariant() switch
    {
        "integer" or "int" or "long" => "integer",
        "number" or "float" or "double" or "decimal" => "number",
        "boolean" or "bool" => "boolean",
        "array" => "array",
        "object" => "object",
        _ => "string",
    };

    private static object ConvertValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => string.Empty,
        _ => value.GetRawText(),
    };
}
