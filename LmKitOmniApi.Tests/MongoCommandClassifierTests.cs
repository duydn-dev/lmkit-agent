using LmKitOmniApi.Infrastructure.AI.Security;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The MongoDB safety gate — the NoSQL analog of the SQL classifier tests. Proves reads
/// stay on the read path, writes are flagged for approval, and code-execution / admin /
/// write-from-read ($out/$merge) operators are refused outright, all without a live DB.
/// </summary>
public sealed class MongoCommandClassifierTests
{
    [Theory]
    [InlineData("{\"collection\":\"orders\",\"op\":\"find\",\"filter\":{\"status\":\"paid\"}}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"findOne\",\"filter\":{}}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"count\",\"filter\":{\"total\":{\"$gt\":100}}}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"distinct\",\"field\":\"status\"}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"aggregate\",\"pipeline\":[{\"$group\":{\"_id\":\"$status\",\"n\":{\"$sum\":1}}}]}")]
    public void Read_Operations_ClassifyAsRead(string command)
    {
        Assert.Equal(MongoCommandKind.Read, MongoCommandClassifier.Classify(command).Kind);
    }

    [Theory]
    [InlineData("{\"collection\":\"orders\",\"op\":\"insertOne\",\"document\":{\"x\":1}}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"updateMany\",\"filter\":{},\"update\":{\"$set\":{\"x\":1}}}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"deleteMany\",\"filter\":{\"x\":1}}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"replaceOne\",\"filter\":{},\"replacement\":{}}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"bulkWrite\"}")]
    public void Write_Operations_ClassifyAsWrite(string command)
    {
        Assert.Equal(MongoCommandKind.Write, MongoCommandClassifier.Classify(command).Kind);
    }

    [Theory]
    [InlineData("{\"collection\":\"orders\",\"op\":\"drop\"}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"dropDatabase\"}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"createIndex\",\"keys\":{\"x\":1}}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"renameCollection\",\"to\":\"x\"}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"mapReduce\"}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"runCommand\"}")]
    public void Admin_Or_Unknown_Operations_AreRefused(string command)
    {
        Assert.Equal(MongoCommandKind.Refused, MongoCommandClassifier.Classify(command).Kind);
    }

    [Theory]
    // $out / $merge persist an aggregation → a write masquerading as a read.
    [InlineData("{\"collection\":\"orders\",\"op\":\"aggregate\",\"pipeline\":[{\"$out\":\"stolen\"}]}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"aggregate\",\"pipeline\":[{\"$merge\":{\"into\":\"x\"}}]}")]
    // Server-side JS / code execution.
    [InlineData("{\"collection\":\"orders\",\"op\":\"find\",\"filter\":{\"$where\":\"this.x==1\"}}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"aggregate\",\"pipeline\":[{\"$match\":{\"$function\":{}}}]}")]
    public void CodeExecution_Or_WriteFromRead_Operators_AreRefused(string command)
    {
        Assert.Equal(MongoCommandKind.Refused, MongoCommandClassifier.Classify(command).Kind);
    }

    [Theory]
    // GAP 1 regression: a dangerous operator written as a \u0024-escaped key must NOT
    // slip past the gate. The raw JSON text contains no literal '$', so the old
    // substring scan missed it; the driver's BsonDocument.Parse decodes it to $where /
    // $out / … at execution time. Inspecting DECODED keys closes the bypass.
    [InlineData("{\"collection\":\"orders\",\"op\":\"find\",\"filter\":{\"\\u0024where\":\"this.x==1\"}}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"aggregate\",\"pipeline\":[{\"\\u0024out\":\"stolen\"}]}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"aggregate\",\"pipeline\":[{\"\\u0024merge\":{\"into\":\"x\"}}]}")]
    [InlineData("{\"collection\":\"orders\",\"op\":\"aggregate\",\"pipeline\":[{\"$match\":{\"\\u0024function\":{\"body\":\"x\",\"args\":[]}}}]}")]
    // Deeply nested inside a legitimate-looking read pipeline.
    [InlineData("{\"collection\":\"orders\",\"op\":\"aggregate\",\"pipeline\":[{\"$project\":{\"v\":{\"\\u0024accumulator\":{}}}}]}")]
    public void UnicodeEscaped_DangerousOperators_AreRefused(string command)
    {
        Assert.Equal(MongoCommandKind.Refused, MongoCommandClassifier.Classify(command).Kind);
    }

    [Theory]
    // A normal read pipeline of safe stages/operators must stay on the read path.
    [InlineData("{\"collection\":\"orders\",\"op\":\"aggregate\",\"pipeline\":[{\"$match\":{\"status\":\"paid\"}},{\"$project\":{\"total\":1}},{\"$group\":{\"_id\":\"$customer\",\"n\":{\"$sum\":1}}},{\"$sort\":{\"n\":-1}},{\"$limit\":10}]}")]
    // A plain find with an operator-bearing filter (but no dangerous operator) is a read.
    [InlineData("{\"collection\":\"orders\",\"op\":\"find\",\"filter\":{\"total\":{\"$gt\":100}}}")]
    // Extended-JSON type wrappers ($oid/$date) are data, not code — must not be refused.
    [InlineData("{\"collection\":\"orders\",\"op\":\"find\",\"filter\":{\"_id\":{\"$oid\":\"507f1f77bcf86cd799439011\"}}}")]
    public void NormalReadPipelines_StayReadable(string command)
    {
        Assert.Equal(MongoCommandKind.Read, MongoCommandClassifier.Classify(command).Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"op\":\"find\"}")]                       // missing collection
    [InlineData("{\"collection\":\"orders\"}")]             // missing op
    [InlineData("{\"collection\":\"\",\"op\":\"find\"}")]   // blank collection
    public void Malformed_Commands_AreRefused(string command)
    {
        Assert.Equal(MongoCommandKind.Refused, MongoCommandClassifier.Classify(command).Kind);
    }

    [Fact]
    public void Classification_ReportsCollectionAndOperation()
    {
        var result = MongoCommandClassifier.Classify("{\"collection\":\"invoices\",\"op\":\"find\",\"filter\":{}}");
        Assert.Equal(MongoCommandKind.Read, result.Kind);
        Assert.Equal("invoices", result.Collection);
        Assert.Equal("find", result.Operation);
    }
}
