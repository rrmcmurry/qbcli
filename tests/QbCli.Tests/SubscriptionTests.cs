using System.Text.Json.Nodes;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class SubscriptionTests
{
    private static SdkPlan Query() => new("13.0", "US", "stopOnError", [new("s1", "DataEventSubscriptionQuery", JsonNode.Parse("{\"SubscriberID\":\"{11111111-1111-1111-1111-111111111111}\"}")!.AsObject())]);
    [Fact] public void SubscriptionUsesSeparateEnvelopeAndNeverBeginsCompanySession()
    {
        var processor = new Fake();
        var result = SubscriptionExecutor.Execute(processor, SdkBatch.Prepare(Query()), null, false);
        Assert.True(result.Success);
        Assert.Equal("local_quickbooks_installation", result.ExecutionScope);
        Assert.False(result.MutatesCompany); Assert.False(result.MutatesSubscriptions);
        Assert.Equal("", result.CompanyFile);
        Assert.True(processor.Opened && processor.Closed && processor.Disposed);
        Assert.Contains("QBXMLSubscriptionMsgsRq", processor.Request);
        Assert.DoesNotContain("onError=", processor.Request);
    }
    [Fact] public void CompanyAndSubscriptionPlansAndResponseEnvelopesCannotMix()
    {
        var plan = Query();
        Assert.Throws<QbException>(() => SdkBatch.Prepare(plan with { Requests = [plan.Requests[0], new("c1", "ClassQuery", new())] }));
        Assert.Throws<QbException>(() => SdkBatch.Responses("<QBXML><QBXMLMsgsRs><DataEventSubscriptionQueryRs requestID='s1' statusCode='1' statusSeverity='Info'/></QBXMLMsgsRs></QBXML>", plan.Requests));
        var output = new StringWriter();
        Assert.Equal(2, CliApplication.Run(["sdk", "run", "DataEventSubscriptionQuery"], (_, _) => throw new Exception("No company connection"), output, new StringWriter()));
        Assert.Throws<QbException>(() => Options.Parse(["sdk", "subscription", "SubscriptionDel", "--apply", "--company-file", Path.GetFullPath("Test.QBW")]));
    }
    [Fact] public void SubscriptionChangePreviewsWithoutCreatingEitherTransport()
    {
        var file=Path.GetTempFileName();
        try
        {
            File.WriteAllText(file,"{\"SubscriberID\":\"{11111111-1111-1111-1111-111111111111}\",\"SubscriptionType\":\"Data\"}");
            var output=new StringWriter();
            Assert.Equal(0,CliApplication.Run(["sdk","subscription","SubscriptionDel","--input",file],(_,_)=>throw new Exception("No company connection"),output,new StringWriter(),subscriptionFactory:()=>throw new Exception("No subscription connection")));
            Assert.Contains("\"submitted\": false",output.ToString());
        }
        finally {File.Delete(file);}
    }
    [Fact] public void FailureClosesAndDisposesSubscriptionConnection()
    {
        var processor=new Fake { Fail=true };
        var result=SubscriptionExecutor.Execute(processor,SdkBatch.Prepare(Query()),null,false);
        Assert.False(result.Success);
        Assert.Equal("unknown",Assert.Single(result.Results).Outcome);
        Assert.True(processor.Closed && processor.Disposed);
    }
    [Fact] public void SubscriptionBatchSplitsWithoutCompanyPolicy()
    {
        var plan=Query();
        var prepared=SdkBatch.Prepare(plan with { Requests=[plan.Requests[0],plan.Requests[0] with { RequestId="s2" }] });
        Assert.Equal(2,SdkBatch.Messages(prepared).Count);
        Assert.Equal(1,prepared.Plan.BatchSize);
        Assert.Throws<QbException>(()=>SdkBatch.Prepare(plan with {Requests=[]}));
    }
    [Fact] public void StatusMessageIsPreservedAsResultData()
    {
        var results=SdkBatch.Responses("<QBXML><QBXMLSubscriptionMsgsRs><DataEventSubscriptionQueryRs requestID='s1' statusCode='1' statusSeverity='Info' statusMessage='No matching &amp; fictional object'/></QBXMLSubscriptionMsgsRs></QBXML>",Query().Requests);
        Assert.Equal("No matching & fictional object",Assert.Single(results).StatusMessage);
    }
    [Fact] public void AppliedSubscriptionChangeReportsInstallationScopeAndJournalsIntent()
    {
        var call=new SdkCall("s1","SubscriptionDel",JsonNode.Parse("{\"SubscriberID\":\"{11111111-1111-1111-1111-111111111111}\",\"SubscriptionType\":\"Data\"}")!.AsObject());
        var prepared=SdkBatch.Prepare(Query() with {Requests=[call]});
        var journal=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".jsonl");
        try
        {
            Assert.Throws<QbException>(()=>SubscriptionExecutor.Execute(new Fake(),prepared,null,false));
            var result=SubscriptionExecutor.Execute(new Fake(),prepared,journal,true);
            Assert.True(result.Success && result.MutatesSubscriptions);
            Assert.False(result.MutatesCompany);
            Assert.Contains("local_quickbooks_installation",File.ReadAllText(journal));
        }
        finally {if(File.Exists(journal))File.Delete(journal);}
    }
    private sealed class Fake : IRequestProcessor, ISubscriptionProcessor
    {
        public bool Opened,Closed,Disposed,Fail;
        public string Request="";
        public void Open()=>Opened=true;
        public void ConfigureAccess() { }
        public string Begin(string file)=>throw new Exception("Subscription must never begin company session");
        public string CurrentCompanyFile(string ticket)=>throw new Exception("No company identity");
        public string Process(string ticket,string request)=>throw new Exception("Wrong transport");
        public string ProcessSubscription(string request)
        {
            Request=request;
            if(Fail)throw new IOException("Synthetic transport failure");
            var rq=System.Xml.Linq.XDocument.Parse(request).Descendants("QBXMLSubscriptionMsgsRq").Single().Elements().Single();
            return "<QBXML><QBXMLSubscriptionMsgsRs><" + rq.Name.LocalName.Replace("Rq","Rs",StringComparison.Ordinal) + " requestID='" + rq.Attribute("requestID")!.Value + "' statusCode='0' statusSeverity='Info'/></QBXMLSubscriptionMsgsRs></QBXML>";
        }
        public void End(string ticket)=>throw new Exception("No company session to end");
        public void Close()=>Closed=true;
        public void Dispose()=>Disposed=true;
    }
}
