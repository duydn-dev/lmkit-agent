using System.Text.Json;

namespace LmKitOmniApi.Application.Widget;

/// <summary>
/// Serialization helpers for <c>TenantWidgetSettings.AllowedOriginsJson</c>.
/// The stored form is a JSON string-array of lowercase origins
/// (<c>["https://a.example","https://b.example"]</c>). Parsing is defensive:
/// malformed JSON, non-array payloads and null entries degrade to an empty list
/// (an empty allowlist denies every origin — fail closed).
/// </summary>
public static class WidgetOrigins
{
    /// <summary>Parses the stored JSON. Never returns null; malformed payloads → empty list.</summary>
    public static List<string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var origins = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } origin)
                    origins.Add(origin);
            }
            return origins;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Serializes a normalized (lowercase, trimmed, de-duplicated, scheme+host only) list.</summary>
    public static string Serialize(IEnumerable<string> origins)
    {
        var normalized = origins
            .Select(Normalize)
            .Where(origin => origin is not null)
            .Select(origin => origin!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return JsonSerializer.Serialize(normalized);
    }

    /// <summary>
    /// Lowercases, trims and reduces an origin to scheme + host[:port] (drops any
    /// path/query). Returns null for values that are not absolute http(s) URLs.
    /// </summary>
    public static string? Normalize(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return null;
        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return null;
        return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}".ToLowerInvariant();
    }
}
