using System.Text;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace LmKitOmniApi.Infrastructure.AI.Database;

/// <summary>
/// The MongoDB (NoSQL) path for the DB agent — separate from the SQL providers because
/// Mongo has no SQL, no fixed schema, and no read-only transaction. Safety mirrors the
/// SQL side: <see cref="MongoCommandClassifier"/> keeps reads on the read path and forces
/// writes through backup-first execution, egress is vetted before any socket opens, and
/// the least-privilege connection account remains the primary guarantee. Schema is
/// sampled live (schemaless store) rather than indexed into Qdrant. Returns agent-facing
/// strings, like <see cref="DbQueryService"/>.
/// </summary>
public sealed class MongoDatabaseService
{
    private readonly DbEgressValidator _egress;
    private readonly DatabaseAgentOptions _options;
    private readonly ILogger<MongoDatabaseService> _logger;

    public MongoDatabaseService(DbEgressValidator egress, IOptions<DatabaseAgentOptions> options, ILogger<MongoDatabaseService> logger)
    {
        _egress = egress;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Whether a connection's provider string routes to this (Mongo) path.</summary>
    public static bool Handles(string? provider) => string.Equals(provider, nameof(DbProvider.Mongo), StringComparison.OrdinalIgnoreCase);

    public string? ExtractHost(string connectionString)
    {
        try { return MongoUrl.Create(connectionString).Server?.Host; }
        catch { return null; }
    }

    /// <summary>Egress-vets then pings; returns the egress denial reason, or null on success. Throws on connect failure.</summary>
    public async Task<string?> TestConnectionAsync(string connectionString, CancellationToken ct)
    {
        var egress = await VetAsync(connectionString, ct);
        if (egress is not null) return egress;

        var (_, database) = Connect(connectionString);
        using var cts = LinkedTimeout(ct);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cts.Token);
        return null;
    }

    /// <summary>Samples each collection to describe its fields (Mongo is schemaless → live sampling).</summary>
    public async Task<string> GetSchemaAsync(string connectionName, string connectionString, CancellationToken ct)
    {
        var egress = await VetAsync(connectionString, ct);
        if (egress is not null) return $"[CSDL] {egress}";

        var (_, database) = Connect(connectionString);
        using var cts = LinkedTimeout(ct);

        var names = await (await database.ListCollectionNamesAsync(cancellationToken: cts.Token)).ToListAsync(cts.Token);
        if (names.Count == 0) return $"[CSDL: {connectionName}] Database chưa có collection nào.";

        var sb = new StringBuilder();
        sb.Append("SCHEMA_CONTEXT_FOR MongoDB: ").AppendLine(connectionName);
        foreach (var name in names.Take(40))
        {
            var collection = database.GetCollection<BsonDocument>(name);
            var sample = await collection.Find(new BsonDocument()).Limit(25).ToListAsync(cts.Token);
            var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var doc in sample)
                foreach (var element in doc.Elements)
                    fields.TryAdd(element.Name, element.Value.BsonType.ToString());

            sb.Append("Collection: ").Append(name).Append(" (~").Append(sample.Count).AppendLine(" doc mẫu)");
            foreach (var field in fields) sb.Append("  - ").Append(field.Key).Append(' ').AppendLine(field.Value);
        }
        sb.AppendLine();
        sb.AppendLine("Hãy gọi run_database_query với MỘT lệnh JSON CHỈ-ĐỌC, ví dụ:");
        sb.AppendLine("{\"collection\":\"<tên>\",\"op\":\"find\",\"filter\":{...},\"limit\":50}");
        sb.AppendLine("hoặc {\"collection\":\"<tên>\",\"op\":\"aggregate\",\"pipeline\":[...]}. Ghi dữ liệu (update/delete/insert) phải được người dùng phê duyệt.");
        return sb.ToString().TrimEnd();
    }

    public async Task<string> RunReadAsync(string connectionName, string connectionString, string commandJson, CancellationToken ct)
    {
        var classification = MongoCommandClassifier.Classify(commandJson);
        switch (classification.Kind)
        {
            case MongoCommandKind.Write:
                return $"[CSDL] Lệnh này GHI dữ liệu nên KHÔNG được tự chạy — cần người dùng phê duyệt (và sao lưu trước). {classification.Reason}\n{commandJson.Trim()}";
            case MongoCommandKind.Refused:
                return $"[CSDL] Lệnh bị từ chối: {classification.Reason}\n{commandJson.Trim()}";
        }

        var egress = await VetAsync(connectionString, ct);
        if (egress is not null) return $"[CSDL] {egress}";

        try
        {
            var command = BsonDocument.Parse(commandJson);
            var (_, database) = Connect(connectionString);
            var collection = database.GetCollection<BsonDocument>(classification.Collection!);
            using var cts = LinkedTimeout(ct);
            var (rows, truncated) = await ExecuteReadAsync(collection, classification.Operation!, command, cts.Token);
            return FormatRows(connectionName, classification.Operation!, classification.Collection!, rows, truncated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mongo read failed for connection '{Connection}'.", connectionName);
            var message = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            return $"[CSDL] Truy vấn thất bại: {message}";
        }
    }

    public async Task<string> RunWriteApprovedAsync(string connectionName, string connectionString, string commandJson, CancellationToken ct)
    {
        var classification = MongoCommandClassifier.Classify(commandJson);
        if (classification.Kind != MongoCommandKind.Write)
            return $"[CSDL] Chỉ thực thi lệnh GHI đã được phê duyệt. {classification.Reason}";

        var egress = await VetAsync(connectionString, ct);
        if (egress is not null) return $"[CSDL] {egress}";

        try
        {
            var command = BsonDocument.Parse(commandJson);
            var (_, database) = Connect(connectionString);
            var collectionName = classification.Collection!;
            var collection = database.GetCollection<BsonDocument>(collectionName);
            using var cts = LinkedTimeout(ct);

            // Back up the whole collection BEFORE writing (never write unbacked).
            var backupName = $"lmkit_backup_{collectionName}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            var backupPipeline = new[] { new BsonDocument("$match", new BsonDocument()), new BsonDocument("$out", backupName) };
            await collection.AggregateAsync<BsonDocument>(backupPipeline, cancellationToken: cts.Token);

            var affected = await ExecuteWriteAsync(collection, classification.Operation!, command, cts.Token);
            return $"[CSDL: {connectionName}] Đã sao lưu collection '{collectionName}' → '{backupName}', rồi thực thi. Số document ảnh hưởng: {affected}.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mongo write failed for connection '{Connection}'.", connectionName);
            var message = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            return $"[CSDL] Ghi thất bại (đã cố sao lưu trước): {message}";
        }
    }

    private async Task<(IReadOnlyList<string> Rows, bool Truncated)> ExecuteReadAsync(
        IMongoCollection<BsonDocument> collection, string op, BsonDocument command, CancellationToken ct)
    {
        var filter = command.TryGetValue("filter", out var f) && f.IsBsonDocument ? f.AsBsonDocument : new BsonDocument();
        var cap = _options.MaxRows;

        switch (op.ToLowerInvariant())
        {
            case "count":
            case "countdocuments":
            case "estimateddocumentcount":
                var count = await collection.CountDocumentsAsync(filter, cancellationToken: ct);
                return (new[] { $"count = {count}" }, false);

            case "distinct":
                var field = command.GetValue("field", "").AsString;
                if (string.IsNullOrWhiteSpace(field)) return (new[] { "Thiếu 'field' cho distinct." }, false);
                var values = await (await collection.DistinctAsync<BsonValue>(field, filter, cancellationToken: ct)).ToListAsync(ct);
                var capped = values.Take(cap).Select(v => v.ToJson()).ToList();
                return (capped, values.Count > cap);

            case "listcollections":
                return (new[] { collection.CollectionNamespace.CollectionName }, false);

            case "aggregate":
                var stages = command.TryGetValue("pipeline", out var p) && p.IsBsonArray
                    ? p.AsBsonArray.Select(s => s.AsBsonDocument).ToList()
                    : new List<BsonDocument>();
                stages.Add(new BsonDocument("$limit", cap + 1)); // +1 to detect truncation
                var agg = await (await collection.AggregateAsync<BsonDocument>(stages, cancellationToken: ct)).ToListAsync(ct);
                return (agg.Take(cap).Select(d => d.ToJson()).ToList(), agg.Count > cap);

            default: // find / findone
                var limit = op.Equals("findone", StringComparison.OrdinalIgnoreCase) ? 1 : cap + 1;
                var docs = await collection.Find(filter).Limit(limit).ToListAsync(ct);
                return (docs.Take(cap).Select(d => d.ToJson()).ToList(), docs.Count > cap);
        }
    }

    private static async Task<long> ExecuteWriteAsync(IMongoCollection<BsonDocument> collection, string op, BsonDocument command, CancellationToken ct)
    {
        var filter = command.TryGetValue("filter", out var f) && f.IsBsonDocument ? f.AsBsonDocument : new BsonDocument();
        var update = command.TryGetValue("update", out var u) && u.IsBsonDocument ? u.AsBsonDocument : new BsonDocument();

        switch (op.ToLowerInvariant())
        {
            case "insertone":
                await collection.InsertOneAsync(command.GetValue("document", new BsonDocument()).AsBsonDocument, cancellationToken: ct);
                return 1;
            case "insertmany":
                var many = command.GetValue("documents", new BsonArray()).AsBsonArray.Select(d => d.AsBsonDocument).ToList();
                await collection.InsertManyAsync(many, cancellationToken: ct);
                return many.Count;
            case "updateone":
                return (await collection.UpdateOneAsync(filter, update, cancellationToken: ct)).ModifiedCount;
            case "updatemany":
                return (await collection.UpdateManyAsync(filter, update, cancellationToken: ct)).ModifiedCount;
            case "replaceone":
                return (await collection.ReplaceOneAsync(filter, command.GetValue("replacement", new BsonDocument()).AsBsonDocument, cancellationToken: ct)).ModifiedCount;
            case "deleteone":
                return (await collection.DeleteOneAsync(filter, ct)).DeletedCount;
            case "deletemany":
                return (await collection.DeleteManyAsync(filter, ct)).DeletedCount;
            default:
                throw new NotSupportedException($"Thao tác ghi '{op}' chưa được hỗ trợ trên đường dẫn này.");
        }
    }

    private async Task<string?> VetAsync(string connectionString, CancellationToken ct)
    {
        var host = ExtractHost(connectionString);
        if (host is null) return "Không đọc được host từ chuỗi kết nối MongoDB.";
        var result = await _egress.ValidateHostAsync(host, ct);
        return result.IsAllowed ? null : result.Reason;
    }

    private (IMongoClient Client, IMongoDatabase Database) Connect(string connectionString)
    {
        var url = MongoUrl.Create(connectionString);
        if (string.IsNullOrWhiteSpace(url.DatabaseName))
            throw new InvalidOperationException("Chuỗi kết nối MongoDB phải chỉ định tên database (…/<database>).");
        var client = new MongoClient(connectionString);
        return (client, client.GetDatabase(url.DatabaseName));
    }

    private CancellationTokenSource LinkedTimeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.QueryTimeoutSeconds));
        return cts;
    }

    private string FormatRows(string connectionName, string op, string collection, IReadOnlyList<string> rows, bool truncated)
    {
        var sb = new StringBuilder();
        sb.Append("[CSDL: ").Append(connectionName).Append("] ").Append(op).Append(' ').AppendLine(collection);
        sb.Append("Kết quả (").Append(rows.Count).Append(" dòng");
        if (truncated) sb.Append(", đã cắt bớt tại ").Append(_options.MaxRows);
        sb.AppendLine("):");
        foreach (var row in rows) sb.AppendLine(row);
        return sb.ToString().TrimEnd();
    }
}
