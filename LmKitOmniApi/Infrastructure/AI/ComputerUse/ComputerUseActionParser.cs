using System.Text.Json;

namespace LmKitOmniApi.Infrastructure.AI.ComputerUse;

/// <summary>
/// Turns the model's next-action output into a typed <see cref="ComputerUseAction"/>.
///
/// It is deliberately TOLERANT of how small local models wrap JSON — leading prose,
/// <c>```json</c> fences, trailing commentary — by extracting the first BALANCED
/// <c>{ … }</c> object from the text and parsing only that. It is deliberately STRICT
/// about the result: an unknown/missing action verb, a malformed object, or a payload
/// missing the fields its verb requires (e.g. <c>navigate</c> without a <c>url</c>) is
/// REJECTED with a reason. It never throws and never guesses a side-effecting action
/// from ambiguous input — a safety property the loop relies on.
/// </summary>
public static class ComputerUseActionParser
{
    /// <summary>
    /// Attempts to parse <paramref name="raw"/> into an action. Returns false (with a
    /// human-readable <paramref name="error"/> and a null <paramref name="action"/>) for
    /// anything empty, unparseable, unknown, or under-specified. Never throws.
    /// </summary>
    public static bool TryParse(string? raw, out ComputerUseAction? action, out string? error)
    {
        action = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Empty model output.";
            return false;
        }

        if (!TryExtractJsonObject(raw, out var json))
        {
            error = "No JSON object found in model output.";
            return false;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            error = "Malformed JSON action.";
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "Action must be a JSON object.";
            return false;
        }

        // The verb may arrive as "action" or "type" (some models prefer one or the other).
        var verb = ReadString(root, "action") ?? ReadString(root, "type");
        if (string.IsNullOrWhiteSpace(verb))
        {
            error = "Missing 'action' field.";
            return false;
        }

        if (!TryMapVerb(verb, out var actionType))
        {
            error = $"Unknown action '{verb}'.";
            return false;
        }

        // Fields can live at the top level or nested under a conventional wrapper
        // ("params"/"parameters"/"args"/"input"); prefer the wrapper when present.
        var fields = FirstObject(root, "params", "parameters", "args", "input") ?? root;

        switch (actionType)
        {
            case ComputerUseActionType.Navigate:
            {
                var url = ReadString(fields, "url") ?? ReadString(root, "url");
                if (string.IsNullOrWhiteSpace(url))
                {
                    error = "navigate requires a 'url'.";
                    return false;
                }
                action = new ComputerUseAction { Type = actionType, Url = url.Trim() };
                return true;
            }

            case ComputerUseActionType.Click:
            {
                var refId = ReadInt(fields, "ref") ?? ReadInt(root, "ref");
                var x = ReadInt(fields, "x") ?? ReadInt(root, "x");
                var y = ReadInt(fields, "y") ?? ReadInt(root, "y");
                if (refId is null && (x is null || y is null))
                {
                    error = "click requires a 'ref' or both 'x' and 'y'.";
                    return false;
                }
                action = new ComputerUseAction { Type = actionType, Ref = refId, X = x, Y = y };
                return true;
            }

            case ComputerUseActionType.Type:
            {
                var refId = ReadInt(fields, "ref") ?? ReadInt(root, "ref");
                var x = ReadInt(fields, "x") ?? ReadInt(root, "x");
                var y = ReadInt(fields, "y") ?? ReadInt(root, "y");
                var text = ReadString(fields, "text") ?? ReadString(root, "text");
                if (text is null)
                {
                    error = "type requires a 'text' value.";
                    return false;
                }
                if (refId is null && (x is null || y is null))
                {
                    error = "type requires a 'ref' or both 'x' and 'y' to target a field.";
                    return false;
                }
                action = new ComputerUseAction { Type = actionType, Ref = refId, X = x, Y = y, Text = text };
                return true;
            }

            case ComputerUseActionType.Key:
            {
                var keys = ReadString(fields, "keys") ?? ReadString(root, "keys")
                    ?? ReadString(fields, "key") ?? ReadString(root, "key");
                if (string.IsNullOrWhiteSpace(keys))
                {
                    error = "key requires a 'keys' value.";
                    return false;
                }
                action = new ComputerUseAction { Type = actionType, Keys = keys.Trim() };
                return true;
            }

            case ComputerUseActionType.Scroll:
            {
                var direction = (ReadString(fields, "direction") ?? ReadString(root, "direction") ?? "down")
                    .Trim().ToLowerInvariant();
                if (direction is not ("up" or "down" or "left" or "right"))
                {
                    error = "scroll 'direction' must be up/down/left/right.";
                    return false;
                }
                var amount = ReadInt(fields, "amount") ?? ReadInt(root, "amount") ?? 3;
                action = new ComputerUseAction { Type = actionType, Direction = direction, Amount = amount };
                return true;
            }

            case ComputerUseActionType.Wait:
            {
                var ms = ReadInt(fields, "ms") ?? ReadInt(root, "ms") ?? 500;
                if (ms < 0) ms = 0;
                action = new ComputerUseAction { Type = actionType, Ms = ms };
                return true;
            }

            case ComputerUseActionType.Screenshot:
                action = new ComputerUseAction { Type = actionType };
                return true;

            case ComputerUseActionType.Done:
            {
                var summary = ReadString(fields, "summary") ?? ReadString(root, "summary")
                    ?? ReadString(fields, "text") ?? string.Empty;
                action = new ComputerUseAction { Type = actionType, Summary = summary };
                return true;
            }

            case ComputerUseActionType.Ask:
            {
                var question = ReadString(fields, "question") ?? ReadString(root, "question")
                    ?? ReadString(fields, "text") ?? string.Empty;
                action = new ComputerUseAction { Type = actionType, Question = question };
                return true;
            }

            default:
                error = $"Unhandled action '{verb}'.";
                return false;
        }
    }

    private static bool TryMapVerb(string verb, out ComputerUseActionType type)
    {
        switch (verb.Trim().ToLowerInvariant())
        {
            case "navigate": case "goto": case "go": type = ComputerUseActionType.Navigate; return true;
            case "click": case "tap": type = ComputerUseActionType.Click; return true;
            case "type": case "fill": case "input": type = ComputerUseActionType.Type; return true;
            case "key": case "press": case "keypress": type = ComputerUseActionType.Key; return true;
            case "scroll": type = ComputerUseActionType.Scroll; return true;
            case "wait": case "sleep": type = ComputerUseActionType.Wait; return true;
            case "screenshot": case "observe": type = ComputerUseActionType.Screenshot; return true;
            case "done": case "finish": case "complete": type = ComputerUseActionType.Done; return true;
            case "ask": case "handoff": case "help": type = ComputerUseActionType.Ask; return true;
            default: type = default; return false;
        }
    }

    /// <summary>
    /// Extracts the first BALANCED brace-delimited object from arbitrary text, ignoring
    /// braces that appear inside JSON string literals (respecting backslash escapes).
    /// Handles <c>```json … ```</c> fences and surrounding prose transparently.
    /// </summary>
    private static bool TryExtractJsonObject(string raw, out string json)
    {
        json = string.Empty;
        var start = raw.IndexOf('{');
        if (start < 0) return false;

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        json = raw.Substring(start, i - start + 1);
                        return true;
                    }
                    break;
            }
        }

        return false; // unbalanced — no complete object
    }

    private static JsonElement? FirstObject(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
                return value;
        }
        return null;
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static int? ReadInt(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
            return n;

        // Tolerate a numeric ref/coord delivered as a string ("3").
        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out var parsed))
            return parsed;

        return null;
    }
}
