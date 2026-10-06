using System.Text.Json.Nodes;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;
public class MessageSetTests
{
    private static SdkPlan Plan(IReadOnlyDictionary<string,string> attributes)=>new("13.0","US","stopOnError",[new("1","ClassQuery",new())],MessageSetAttributes:attributes);
    [Fact] public void RecoveryControlsRequireApplyAndArePartOfDigest()
    {
        var plain=SdkBatch.Prepare(Plan(new Dictionary<string,string>()));
        var recovery=SdkBatch.Prepare(Plan(new Dictionary<string,string>{{"newMessageSetID","A123"},{"responseData","includeNone"}}));
        Assert.True(recovery.RequiresApply);
        Assert.NotEqual(plain.Digest,recovery.Digest);
        Assert.Contains("newMessageSetID=\"A123\"",SdkBatch.Messages(recovery)[0].Xml);
        Assert.Contains("responseData=\"includeNone\"",SdkBatch.Messages(recovery)[0].Xml);
    }
    [Theory]
    [InlineData("newMessageSetID","")]
    [InlineData("newMessageSetID","has:colon")]
    [InlineData("oldMessageSetID","has/slash")]
    [InlineData("newMessageSetID","123456789012345678901234")]
    [InlineData("responseData","everything")]
    [InlineData("arbitrary","value")]
    public void InvalidMessageControlsFailBeforeConnection(string key,string value)=>Assert.Throws<QbException>(()=>SdkBatch.Prepare(Plan(new Dictionary<string,string>{{key,value}})));
    [Fact] public void RecoveryIDsCannotBeSilentlyReusedForMultipleBatches()
    {
        var plan=Plan(new Dictionary<string,string>{{"newMessageSetID","single"}});
        Assert.Throws<QbException>(()=>SdkBatch.Prepare(plan with {BatchSize=1,Requests=[plan.Requests[0],plan.Requests[0] with {RequestId="2"}]}));
    }
    [Fact] public void MessageStatusIsReportedAndCannotBeMaskedByRequestSuccess()
    {
        var prepared=SdkBatch.Prepare(Plan(new Dictionary<string,string>()));
        var result=SdkBatch.Execute(prepared,"test.QBW",null,_=>"<QBXML><QBXMLMsgsRs messageSetStatusCode='9001'><ClassQueryRs requestID='1' statusCode='0' statusSeverity='Info'/></QBXMLMsgsRs></QBXML>");
        Assert.False(result.Success);
        Assert.Equal("9001",Assert.Single(result.MessageSetResponses!).Attributes["messageSetStatusCode"]);
        Assert.Equal("succeeded",Assert.Single(result.Results).Outcome);
    }
    [Fact] public void ClearingRecoveryStateDoesNotClaimAnAccountingMutation()
    {
        var prepared=SdkBatch.Prepare(Plan(new Dictionary<string,string>{{"oldMessageSetID","previous"}}));
        var result=SdkBatch.Execute(prepared,"test.QBW",null,_=>"<QBXML><QBXMLMsgsRs oldMessageSetID='previous' messageSetStatusCode='0'><ClassQueryRs requestID='1' statusCode='0' statusSeverity='Info'/></QBXMLMsgsRs></QBXML>");
        Assert.True(result.Success && result.MutatesRecoveryState);
        Assert.False(result.MutatesCompany);
    }
    [Fact] public void EmptyClearEnvelopeRequiresAnAcknowledgementAndNeverClaimsMissingRequestsSucceeded()
    {
        var prepared=SdkBatch.Prepare(Plan(new Dictionary<string,string>{{"oldMessageSetID","previous"}}) with {Requests=[]});
        Assert.True(prepared.RequiresApply);
        Assert.Single(SdkBatch.Messages(prepared));
        var success=SdkBatch.Execute(prepared,"test.QBW",null,_=>"<QBXML><QBXMLMsgsRs messageSetStatusCode='0'/></QBXML>");
        Assert.True(success.Success && success.MutatesRecoveryState); Assert.Empty(success.Results);
        var absent=SdkBatch.Execute(prepared,"test.QBW",null,_=>"<QBXML><QBXMLMsgsRs/></QBXML>");
        Assert.False(absent.Success);
        var failure=SdkBatch.Execute(prepared,"test.QBW",null,_=>throw new IOException());
        Assert.False(failure.Success);
        var statusOnly=SdkBatch.Execute(SdkBatch.Prepare(Plan(new Dictionary<string,string>{{"newMessageSetID","saved"}})),"test.QBW",null,_=>"<QBXML><QBXMLMsgsRs messageSetStatusCode='0'/></QBXML>");
        Assert.False(statusOnly.Success);Assert.Equal("unknown",Assert.Single(statusOnly.Results).Outcome);
    }
}
