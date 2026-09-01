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
