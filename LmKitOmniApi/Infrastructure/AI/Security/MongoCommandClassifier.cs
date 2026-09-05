using System.Text.Json;

namespace LmKitOmniApi.Infrastructure.AI.Security;

public enum MongoCommandKind
{
    Read,
    Write,
    Refused
}

/// <summary>Outcome of classifying an agent-issued MongoDB command.</summary>
public sealed record MongoClassification(MongoCommandKind Kind, string Reason, string? Collection = null, string? Operation = null);

/// <summary>
/// Deterministic safety gate for the MongoDB path — the NoSQL analog of
/// <see cref="SqlStatementClassifier"/>. MongoDB has no SQL, so the agent emits a small
/// JSON command (<c>{ "collection": "...", "op": "find|aggregate|count|distinct", ... }</c>);
/// this classifies it as read, write, or refused BEFORE any driver call. Like the SQL
/// classifier it is defense-in-depth (the least-privilege DB account is the real gate),
/// but it deterministically keeps reads on the read path and forces writes through the
/// HITL-approval + backup path — and outright refuses code-execution / admin operators.
/// </summary>
public static class MongoCommandClassifier
{
    private static readonly HashSet<string> ReadOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "find", "findone", "count", "countdocuments", "estimateddocumentcount", "distinct", "aggregate", "listcollections"
    };

    private static readonly HashSet<string> WriteOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "insertone", "insertmany", "updateone", "updatemany", "replaceone",
        "deleteone", "deletemany", "findoneandupdate", "findoneandreplace", "findoneanddelete", "bulkwrite"
    };

    // Operators that execute server-side code or write from inside an otherwise-"read"
    // command ($out/$merge persist an aggregation). Refused regardless of the op.
    // Matched as DECODED keys (see FindDangerousOperator) so a unicode-escaped form such
    // as "$where" is caught — the raw JSON text never contains the literal '$'.
    private static readonly HashSet<string> DangerousOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "$where", "$function", "$accumulator", "$out", "$merge", "$eval"
    };

    public static MongoClassification Classify(string? commandJson)
    {
        if (string.IsNullOrWhiteSpace(commandJson))
            return new MongoClassification(MongoCommandKind.Refused, "Lệnh Mongo rỗng.");

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(commandJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new MongoClassification(MongoCommandKind.Refused, "Lệnh Mongo phải là JSON hợp lệ.");
        }

        if (root.ValueKind != JsonValueKind.Object)
            return new MongoClassification(MongoCommandKind.Refused, "Lệnh Mongo phải là một đối tượng JSON.");

        // Code-exec / write-from-read operators anywhere in the command → refuse outright.
        // Inspect the PARSED/DECODED command, never the raw text: System.Text.Json
        // unescapes "$where" to the key "$where" (exactly as the driver's
        // BsonDocument.Parse will at execution time), so walking decoded keys closes the
        // unicode-escape bypass a raw-substring scan left open. Walk recurses through
        // nested documents AND array elements (aggregation pipeline stages).
        var banned = FindDangerousOperator(root);
        if (banned is not null)
            return new MongoClassification(MongoCommandKind.Refused, $"Toán tử '{banned}' không được phép.");

        var collection = GetString(root, "collection");
        if (string.IsNullOrWhiteSpace(collection) || collection.Length > 200)
            return new MongoClassification(MongoCommandKind.Refused, "Thiếu tên collection hợp lệ.");

        var operation = GetString(root, "op") ?? GetString(root, "operation");
        if (string.IsNullOrWhiteSpace(operation))
            return new MongoClassification(MongoCommandKind.Refused, "Thiếu 'op' (thao tác).");

        if (ReadOps.Contains(operation))
            return new MongoClassification(MongoCommandKind.Read, "Thao tác chỉ-đọc.", collection, operation);

        if (WriteOps.Contains(operation))
            return new MongoClassification(MongoCommandKind.Write, "Thao tác ghi — cần phê duyệt và sao lưu.", collection, operation);

        // drop, dropDatabase, createIndex, renameCollection, mapReduce, runCommand, …
        return new MongoClassification(MongoCommandKind.Refused, $"Thao tác '{operation}' không được hỗ trợ.", collection, operation);
    }

    /// <summary>
    /// Depth-first search for a banned operator by its DECODED key name, recursing
    /// through every nested object AND array element. Because it inspects parsed JSON, a
    /// key written as the unicode escape "$where" is compared as "$where" — the form
    /// the driver executes — so the escape cannot smuggle a dangerous operator past a
    /// raw-text check. Only KEYS are matched (Mongo operators are always keys); a string
    /// VALUE that merely contains "$where" is harmless data and is deliberately ignored to
    /// avoid false refusals. Returns the offending decoded key, or null if none is found.
    /// </summary>
    private static string? FindDangerousOperator(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (DangerousOperators.Contains(property.Name))
                        return property.Name;
                    var nested = FindDangerousOperator(property.Value);
                    if (nested is not null) return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindDangerousOperator(item);
                    if (nested is not null) return nested;
                }
                break;
        }
        return null;
    }

    private static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
