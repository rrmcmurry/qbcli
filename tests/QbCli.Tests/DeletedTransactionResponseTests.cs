using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class DeletedTransactionResponseTests
{
    [Theory]
    [InlineData("ReceivePaymentQuery")]
    [InlineData("BillPaymentCheckQuery")]
    [InlineData("CreditMemoQuery")]
    [InlineData("VendorCreditQuery")]
    public void EmptyDeletedIdWarningIsNotConvertedToSuccessfulNoMatch(string operation)
    {
        var xml=$"<QBXML><QBXMLMsgsRs><{operation}Rs requestID='missing' statusCode='500' statusSeverity='Warn' statusMessage='Requested transaction could not be found.'/></QBXMLMsgsRs></QBXML>";
        var result=Assert.Single(SdkBatch.Responses(xml,[new SdkCall("missing",operation,new())]));
        Assert.Equal("warning",result.Outcome);
        Assert.Equal(500,result.StatusCode);
        Assert.Equal("Warn",result.Severity);
        Assert.Empty(result.Data);
        Assert.Equal("Requested transaction could not be found.",result.StatusMessage);
    }

    [Fact]
    public void DeletionAcknowledgementRetainsTopLevelScalarsAndOptionalReference()
    {
        var xml="<QBXML><QBXMLMsgsRs><TxnDelRs requestID='delete' statusCode='0' statusSeverity='Info'><TxnDelType>BillPaymentCheck</TxnDelType><TxnID>deleted-payment</TxnID><TimeDeleted>2026-10-05T23:10:28-05:00</TimeDeleted><RefNumber>4</RefNumber></TxnDelRs></QBXMLMsgsRs></QBXML>";
        var result=Assert.Single(SdkBatch.Responses(xml,[new SdkCall("delete","TxnDel",new())]));
        Assert.Equal("succeeded",result.Outcome);
        Assert.Equal(new[]{"TxnDelType","TxnID","TimeDeleted","RefNumber"},result.Data.Select(n=>n.Name));
        Assert.Equal("deleted-payment",result.Data.Single(n=>n.Name=="TxnID").Value);
        Assert.All(result.Data,n=>Assert.Empty(n.Children));
    }
}
