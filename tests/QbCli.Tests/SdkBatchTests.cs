using System.Text.Json.Nodes;
using System.Xml.Linq;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class SdkBatchTests
{
    [Theory]
    [InlineData("1024",true)] [InlineData("16777216",true)]
    [InlineData("1023",false)] [InlineData("16777217",false)]
    [InlineData("\"1024\"",false)] [InlineData("null",false)]
    [InlineData("1024.5",false)] [InlineData("true",false)]
    public void BatchInputAcceptsOnlyBoundedIntegerByteLimits(string value,bool allowed)
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");
        try
        {
            File.WriteAllText(path,"{\"maximumBatchBytes\":"+value+",\"requests\":[{\"operation\":\"ClassQuery\",\"fields\":{}}]}");
            var options=Options.Parse(["sdk","batch","--input",path]);
            if(allowed)Assert.Equal(int.Parse(value),SdkBatch.Prepare(SdkInput.Plan(options)).Plan.MaximumBatchBytes);
            else
            {
                var output=new StringWriter();
                Assert.NotEqual(0,CliApplication.Run(["sdk","batch","--input",path],(_,_)=>throw new Exception("Must reject before connection"),output,new StringWriter()));
                Assert.Contains("invalid_arguments",output.ToString());
            }
        }
        finally{File.Delete(path);}
    }
    [Fact] public void ByteBoundsMeasureEscapedUnicodeAndPreserveEveryRequest()
    {
        var calls=Enumerable.Range(0,40).Select(i=>new SdkCall(i.ToString(),"CustomerQuery",
            new JsonObject{{"FullName",string.Concat(Enumerable.Repeat("租&<\"",30))}})).ToArray();
        var prepared=SdkBatch.Prepare(new("13.0","US","continueOnError",calls,1000,1024));
        var batches=SdkBatch.Messages(prepared);
        Assert.True(batches.Count>1);
        var ids=new List<string>();
        foreach(var (start,xml) in batches)
        {
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(xml),1,1024);
            var requests=XDocument.Parse(xml).Descendants("CustomerQueryRq").ToArray();
            Assert.Equal(ids.Count,start);
            ids.AddRange(requests.Select(r=>r.Attribute("requestID")!.Value));
            Assert.All(requests,r=>Assert.Equal(calls[0].Fields["FullName"]!.GetValue<string>(),r.Element("FullName")!.Value));
            if(start+requests.Length<calls.Length)
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(SdkBatch.Message(prepared.Plan,prepared.Requests.Skip(start).Take(requests.Length+1)))>1024);
        }
        Assert.Equal(calls.Select(c=>c.RequestId),ids);
    }
    private static SdkCall Call(string id) => new(id, "ClassQuery", new JsonObject());
    private static string Response(string id, int code = 0, string severity = "Info") => $"<ClassQueryRs requestID='{id}' statusCode='{code}' statusSeverity='{severity}'/>";
    private static string Envelope(string body) => "<QBXML><QBXMLMsgsRs>" + body + "</QBXMLMsgsRs></QBXML>";
    [Fact] public void CorrelatesOutOfOrderResponsesAndReportsMissingOnesUnknown()
    {
        var result = SdkBatch.Responses(Envelope(Response("b") + Response("a")), [Call("a"), Call("b"), Call("c")]);
        Assert.Equal(new[] { "a", "b", "c" }, result.Select(o => o.RequestId));
        Assert.Equal("unknown", result[2].Outcome);
    }
    [Fact] public void DuplicateAndForeignResponseIdsAreRejected()
    {
        Assert.Throws<QbException>(() => SdkBatch.Responses(Envelope(Response("a") + Response("a")), [Call("a")]));
        Assert.Throws<QbException>(() => SdkBatch.Responses(Envelope(Response("other")), [Call("a")]));
    }
    [Fact] public void PlanRejectsDuplicateIdsAndBoundsBatches()
    {
        Assert.Throws<QbException>(() => SdkBatch.Prepare(new("13.0", "US", "stopOnError", [Call("a"), Call("a")])));
        var prepared = SdkBatch.Prepare(new("13.0", "US", "stopOnError", Enumerable.Range(0, 5).Select(i => Call(i.ToString())).ToArray(), BatchSize: 2));
        Assert.Equal(new[] { 0, 2, 4 }, SdkBatch.Messages(prepared).Select(b => b.Start));
    }
    [Fact] public void UncertainSubmissionIsJournaledAndNeverRetried()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            var prepared = SdkBatch.Prepare(new("13.0", "US", "stopOnError", [Call("a"), Call("b")], BatchSize: 1));
            int attempts = 0;
            var result = SdkBatch.Execute(prepared, "test.QBW", path, _ => { attempts++; throw new IOException("Could have been applied"); });
            Assert.False(result.Success); Assert.Equal(1, attempts);
            Assert.Equal(new[] { "unknown", "unprocessed" }, result.Results.Select(o => o.Outcome));
            var records = File.ReadLines(path).Select(line => JsonNode.Parse(line)!).ToArray();
            Assert.Equal("submitted", records[1]["kind"]!.GetValue<string>());
            Assert.Equal("finished", records[^1]["kind"]!.GetValue<string>());
            Assert.Throws<IOException>(() => SdkBatch.Execute(prepared, "test.QBW", path, _ => throw new Exception("Must not submit")));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Fact] public void PartialSuccessAndUnprocessedRequestsRemainDistinct()
    {
        var prepared = SdkBatch.Prepare(new("13.0", "US", "stopOnError", [Call("a"), Call("b"), Call("c")]));
        var result = SdkBatch.Execute(prepared, "test.QBW", null, _ => Envelope(Response("a") + Response("b", 3100, "Error") + Response("c", 3231, "Error")));
        Assert.False(result.Success);
        Assert.Equal(new[] { "succeeded", "failed", "unprocessed" }, result.Results.Select(o => o.Outcome));
    }
}
