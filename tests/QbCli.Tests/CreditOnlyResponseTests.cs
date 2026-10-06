using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;
namespace QbCli.Tests;
public class CreditOnlyResponseTests
{
    [Theory]
    [InlineData("ReceivePaymentAdd","ReceivePaymentRet","Invoice")]
    [InlineData("BillPaymentCheckAdd","BillPaymentCheckRet","Bill")]
    [InlineData("BillPaymentCreditCardAdd","BillPaymentCreditCardRet","Bill")]
    public void AcceptedLeanApplicationDoesNotRequireCreatedPaymentIdOrInventAmount(string operation,string record,string target)
    {
        var response=$"<QBXML><QBXMLMsgsRs><{operation}Rs requestID='credit' statusCode='0' statusSeverity='Info'><{record}><AppliedToTxnRet><TxnID>existing-document</TxnID><TxnType>{target}</TxnType></AppliedToTxnRet></{record}></{operation}Rs></QBXMLMsgsRs></QBXML>";
        var result=SdkBatch.Responses(response,[new SdkCall("credit",operation,new())]).Single();
        Assert.Equal("succeeded",result.Outcome);Assert.Equal(0,result.StatusCode);
        var data=Assert.Single(result.Data);Assert.DoesNotContain(data.Children,c=>c.Name=="TxnID");
        var application=Assert.Single(data.Children);
        Assert.Equal("existing-document",application.Children.Single(c=>c.Name=="TxnID").Value);
        Assert.DoesNotContain(application.Children,c=>c.Name is "Amount" or "BalanceRemaining");
    }
}
