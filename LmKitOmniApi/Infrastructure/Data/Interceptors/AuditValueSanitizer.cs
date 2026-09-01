namespace LmKitOmniApi.Infrastructure.Data.Interceptors;

public static class AuditValueSanitizer
{
    private static readonly string[] RedactedNameFragments =
    [
        "password", "secret", "token", "apikey", "cryptokey", "keypem",
        "content", "memoryvalue", "sourcecontext", "parametersjson",
        "email", "fullname", "username"
    ];

    public static object? Sanitize(string propertyName, object? value)
    {
        var normalized = propertyName.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (RedactedNameFragments.Any(normalized.Contains)) return "[REDACTED]";

        if (value is string text && text.Length > 512)
            return $"{text[..512]}…[TRUNCATED]";

        return value;
    }
}
