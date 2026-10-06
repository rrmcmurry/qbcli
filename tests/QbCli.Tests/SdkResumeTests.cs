using System.Text.Json.Nodes;
using System.Xml.Linq;
using QbCli;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class SdkResumeTests
{
    [Fact] public void ReadsExecutionHistoryLargerThanTypedInputWithoutReplayingCompletedRows()
    {
        var lines=Journal(i=>i==1?3100:0);
        var path=Path.GetTempFileName();
        try
        {
            // Valid JSON whitespace makes this a genuine >64 MiB file without
            // inventing outcomes or weakening identity/digest checks.
            using(var writer=new StreamWriter(path,false,new System.Text.UTF8Encoding(false)))
            {
                var padding=new string(' ',70*1024*1024/lines.Length+1);
                foreach(var line in lines){writer.Write(line);writer.Write(padding);writer.WriteLine();}
            }
            Assert.True(new FileInfo(path).Length>64*1024*1024);
            var resume=SdkResume.Read(path);
            Assert.Equal(["1"],resume.ConfirmedCompleted);
            Assert.Equal(["2"],resume.FailedExcluded);
            Assert.Equal("3",Assert.Single(resume.PendingPlan.Requests).RequestId);
            Assert.True(resume.CanApply);
        }
        finally{File.Delete(path);}
    }
    [Fact] public void RejectsBadHeaderBeforeEnumeratingLaterRecords()
    {
        IEnumerable<string> Records()
        {
            yield return "{\"kind\":\"outcome\",\"executionScope\":\"company\"}";
            throw new InvalidOperationException("Later records must not be read before header validation.");
        }
        Assert.Throws<QbException>(()=>SdkResume.Build(Records()));
    }
    [Fact] public void StreamingRejectsBlankLaterRecords()
    {
        var lines=Journal(_=>0);
        Assert.Throws<QbException>(()=>SdkResume.Build(lines.Append(" ")));
    }
    private static string[] Journal(Func<int,int> status, bool uncertain = false, bool includeProcess = false)
    {
        var plan = new SdkPlan("13.0","US","stopOnError",Enumerable.Range(1,3).Select(i => new SdkCall(i.ToString(),"ClassAdd",new() { ["ClassAdd"] = new JsonObject { ["Name"] = "Class"+i } })).ToArray(),1);
        var path = Path.GetTempFileName(); File.Delete(path);
        try
        {
            int batch = 0;
            SdkBatch.Execute(SdkBatch.Prepare(plan),"C:\\test\\Test.QBW",path,xml =>
            {
                if (uncertain) throw new IOException("Interrupted");
                var doc = XDocument.Parse(xml); string id = (string)doc.Descendants("ClassAddRq").Single().Attribute("requestID")!;
                int code = status(batch++);
                return $"<QBXML><QBXMLMsgsRs><ClassAddRs requestID='{id}' statusCode='{code}' statusSeverity='{(code==0?"Info":"Error")}' statusMessage='Synthetic'/></QBXMLMsgsRs></QBXML>";
            });
            var lines = File.ReadAllLines(path);
            if (!includeProcess)
            {
                var header = JsonNode.Parse(lines[0])!.AsObject(); header.Remove("sourceProcess");
                lines[0] = header.ToJsonString(); // Existing journals before process provenance was added.
            }
            return lines;
        }
        finally { File.Delete(path); }
    }
    [Fact] public void SkipsCompletedAndFailedAndSelectsNeverSubmittedSuffix()
    {
        var resume = SdkResume.Build(Journal(i => i==1 ? 3100 : 0));
        Assert.True(resume.CanApply); Assert.Equal(["1"],resume.ConfirmedCompleted); Assert.Equal(["2"],resume.FailedExcluded);
        Assert.Equal("3",resume.PendingPlan.Requests.Single().RequestId);
    }
    [Fact] public void UnknownSubmissionBlocksEvenNeverSubmittedRemainder()
    {
        var resume = SdkResume.Build(Journal(_ => 0,true));
        Assert.False(resume.CanApply); Assert.Equal(["1"],resume.RequiresReconciliation);
        Assert.Equal(["2","3"],resume.PendingPlan.Requests.Select(c=>c.RequestId));
    }
    [Fact] public void CrashAfterSubmissionIntentRemainsUnknown()
    {
        var lines = Journal(_ => 0);
        var resume = SdkResume.Build(lines.Take(2).ToArray());
        Assert.False(resume.CanApply); Assert.Equal(["1"],resume.RequiresReconciliation);
    }
    [Fact] public void SDKUnprocessedAcknowledgementIsEligible()
    {
        var resume = SdkResume.Build(Journal(_ => 3231));
        Assert.True(resume.CanApply); Assert.Equal(3,resume.PendingPlan.Requests.Count);
    }
    [Fact] public void FullyCompletedJournalHasNoResidualWork()
    {
        var resume = SdkResume.Build(Journal(_ => 0));
        Assert.True(resume.CanApply); Assert.Empty(resume.PendingPlan.Requests); Assert.Equal(3,resume.ConfirmedCompleted.Count);
    }
    [Fact] public void RejectsTamperedPlanSubmissionAndDuplicateOutcome()
    {
        var lines = Journal(_ => 0);
        var plan = JsonNode.Parse(lines[0])!.AsObject(); plan["requests"]![0]!["fields"]!["ClassAdd"]!["Name"]="Altered";
        Assert.Throws<QbException>(()=>SdkResume.Build([plan.ToJsonString(),..lines.Skip(1)]));
        var intent = JsonNode.Parse(lines[1])!.AsObject(); intent["requestXml"]="<Other/>";
        Assert.Throws<QbException>(()=>SdkResume.Build([lines[0],intent.ToJsonString(),..lines.Skip(2)]));
        string outcome = lines.First(l=>JsonNode.Parse(l)!["kind"]!.GetValue<string>()=="outcome");
        Assert.Throws<QbException>(()=>SdkResume.Build([..lines.Take(lines.Length-1),outcome,lines[^1]]));
    }
    [Fact] public void UnknownResumeApplyNeverConnects()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path,Journal(_=>0,true));
            var output = new StringWriter();
            Assert.Equal(4,CliApplication.Run(["sdk","resume","--input",path,"--company-file","C:\\test\\Test.QBW","--apply"],(_,_)=>throw new Exception("Must not connect"),output,new StringWriter()));
            Assert.Contains("reconciliation_required",output.ToString());
        }
        finally { File.Delete(path); }
    }
    [Fact] public void NonzeroMessageEnvelopeBlocksResumeDespiteSuccessfulRequestStatuses()
    {
        var lines = Journal(_=>0);
        int index = Array.FindIndex(lines,l=>JsonNode.Parse(l)!["kind"]!.GetValue<string>()=="message_set_response");
        var record=JsonNode.Parse(lines[index])!.AsObject();
        record["result"]!["attributes"]!["messageSetStatusCode"]="9001";
        lines[index]=record.ToJsonString();
        var resume=SdkResume.Build(lines);
        Assert.False(resume.CanApply); Assert.Contains("1",resume.RequiresReconciliation);
    }

    [Fact] public void FinishedExecutionDoesNotEstablishProcessExit()
    {
        var resume = SdkResume.Build(Journal(i => i == 1 ? 3100 : 0, includeProcess:true));
        Assert.Equal("running",resume.SourceProcessState); Assert.False(resume.CanApply);
        Assert.Empty(resume.RequiresReconciliation); Assert.Single(resume.PendingPlan.Requests);
        Assert.Equal(Environment.ProcessId,resume.SourceProcess!.ProcessId);
    }

    [Fact] public void LiveOriginatingProcessBlocksApplyBeforeFactory()
    {
        var path=Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path,Journal(_=>3231,includeProcess:true));
            var output=new StringWriter();
            Assert.Equal(4,CliApplication.Run(["sdk","resume","--input",path,"--company-file",@"C:\test\Test.QBW","--apply"],
                (_,_)=>throw new Exception("Must not connect"),output,new StringWriter()));
            Assert.Contains("source_process_running",output.ToString());
        }
        finally {File.Delete(path);}
    }

    [Fact] public void ReusedPidDoesNotImplyOriginalProcessIsRunning()
    {
        var current=SdkJournalProcess.Current();
        var differentStart=DateTime.Parse(current.StartTimeUtc,System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind).AddTicks(-1).ToString("O",System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("stopped",SdkJournalProcess.Observe(current with {StartTimeUtc=differentStart}));
    }

    [Theory] [InlineData("null")] [InlineData("{}")] [InlineData("{\"processId\":0,\"startTimeUtc\":\"invalid\"}")]
    [InlineData("{\"processId\":1,\"startTimeUtc\":\"2026-10-05T00:00:00.0000000\"}")]
    public void MalformedProcessIdentityIsNotTreatedAsStopped(string owner)
    {
        var lines=Journal(_=>0); var header=JsonNode.Parse(lines[0])!.AsObject();
        header["sourceProcess"]=JsonNode.Parse(owner); lines[0]=header.ToJsonString();
        Assert.Throws<QbException>(()=>SdkResume.Build(lines));
    }
}
