using System.Text.Json.Nodes;
using System.Xml.Linq;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class SdkIteratorTests
{
    private const string Company = @"C:\Test.QBW";
    private const string Iterator = "{11111111-1111-1111-1111-111111111111}";
    private static SdkPlan Plan(JsonObject? fields = null, string operation = "TransactionQuery") =>
        new("13.0", "US", "stopOnError", [new("scan", operation, fields ?? new())]);
    private static XElement Transaction(string? line, string amount = "1.00", bool identity = true, string? account = null) =>
        new("TransactionRet", identity ? new XElement("TxnType", "Deposit") : null,
            identity ? new XElement("TxnID", "same-transaction") : null,
            line is null ? null : new XElement("TxnLineID", line), new XElement("Amount", amount),
            account is null?null:new XElement("AccountRef",new XElement("ListID",account)));

    [Fact] public void InventoryPostingsWithSharedLineIdentityKeepDistinctAccounts()
    {
        var processor=new Processor([Page("TransactionQuery",2,Transaction("line",account:"income")),
            Page("TransactionQuery",1,Transaction("line",account:"asset")),Page("TransactionQuery",0,Transaction("line",account:"cogs"))]);
        using var reader=new QuickBooksReader(processor,Company);
        var result=reader.QuerySdkAll(Plan(),Company,1);
        Assert.True(result.Complete); Assert.Equal(3,result.Data.Count);
    }
    [Fact] public void RepeatedAccountPostingStillFailsAcrossPages()
    {
        using var reader=new QuickBooksReader(new Processor([Page("TransactionQuery",1,Transaction("line",account:"income")),
            Page("TransactionQuery",0,Transaction("line","1.0",account:"income"))]),Company);
        Assert.Equal("incomplete_response",Assert.Throws<QbException>(()=>reader.QuerySdkAll(Plan(),Company,1)).Code);
    }
    [Fact] public void SummaryDuplicatesCannotHideBehindDifferentAccounts()
    {
        using var reader=new QuickBooksReader(new Processor([Page("TransactionQuery",1,Transaction(null,account:"first")),
            Page("TransactionQuery",0,Transaction(null,account:"second"))]),Company);
        Assert.Throws<QbException>(()=>reader.QuerySdkAll(Plan(),Company,1));
    }
    [Fact] public void AccountOnlyProjectionUsesInternalIdsButRetainsSelectedAccount()
    {
        var processor=new Processor([Page("TransactionQuery",1,Transaction("line",account:"income")),Page("TransactionQuery",0,Transaction("line",account:"cogs"))]);
        using var reader=new QuickBooksReader(processor,Company);
        var result=reader.QuerySdkAll(Plan(new() {["IncludeRetElement"]="AccountRef"}),Company,1);
        Assert.All(result.Data,n=>Assert.Equal(new[]{"AccountRef"},n.Children.Select(c=>c.Name)));
        Assert.All(processor.Requests,r=>Assert.Single(r.Descendants("IncludeRetElement"),n=>n.Value=="AccountRef"));
        Assert.All(processor.Requests,r=>Assert.Single(r.Descendants("IncludeRetElement"),n=>n.Value=="Amount"));
    }
    [Fact] public void InventoryCreditsKeepDistinctAmountsOnSameAccountAndLineAcrossPages()
    {
        var processor=new Processor([Page("TransactionQuery",1,Transaction("line","11.00",account:"asset")),Page("TransactionQuery",0,Transaction("line","-5.63",account:"asset"))]);
        using var reader=new QuickBooksReader(processor,Company);
        var result=reader.QuerySdkAll(Plan(),Company,1);
        Assert.True(result.Complete);Assert.Equal(2,result.Data.Count);
    }
    [Theory] [InlineData("NaN")] [InlineData("1,000.00")] [InlineData("")]
    public void InvalidPostingAmountCannotBeUsedAsIdentity(string amount)
    {
        using var reader=new QuickBooksReader(new Processor([Page("TransactionQuery",0,Transaction("line",amount,account:"asset"))]),Company);
        Assert.Throws<QbException>(()=>reader.QuerySdkAll(Plan(),Company,1));
    }
    [Fact] public void DuplicateAmountIdentityFieldsAreRejected()
    {
        var record=Transaction("line",account:"asset");record.Add(new XElement("Amount","2.00"));
        using var reader=new QuickBooksReader(new Processor([Page("TransactionQuery",0,record)]),Company);
        Assert.Throws<QbException>(()=>reader.QuerySdkAll(Plan(),Company,1));
    }
    [Fact] public void NamedPostingAccountWithoutListIdFailsIdentityCheck()
    {
        var record=Transaction("line"); record.Add(new XElement("AccountRef",new XElement("FullName","Missing ID")));
        using var reader=new QuickBooksReader(new Processor([Page("TransactionQuery",0,record)]),Company);
        Assert.Throws<QbException>(()=>reader.QuerySdkAll(Plan(),Company,1));
    }
    private static string Page(string operation, int remaining, XElement record) => new XElement("QBXML",
        new XElement("QBXMLMsgsRs", new XElement(operation + "Rs", new XAttribute("requestID", "scan"),
            new XAttribute("statusCode", 0), new XAttribute("statusSeverity", "Info"),
            new XAttribute("iteratorRemainingCount", remaining), new XAttribute("iteratorID", Iterator), record))).ToString();

    [Fact] public void SummaryAndMultipleLinesShareTransactionIdAcrossPages()
    {
        var processor = new Processor([Page("TransactionQuery", 2, Transaction(null)),
            Page("TransactionQuery", 1, Transaction("line1")), Page("TransactionQuery", 0, Transaction("line2"))]);
        using var reader = new QuickBooksReader(processor, Company);
        var result = reader.QuerySdkAll(Plan(), Company, 1);
        Assert.True(result.Complete);
        Assert.Equal(3, result.Pages);
        Assert.Equal(3, result.Data.Count);
        Assert.Equal("Start", processor.Requests[0].Descendants("TransactionQueryRq").Single().Attribute("iterator")!.Value);
        Assert.Equal("Continue", processor.Requests[1].Descendants("TransactionQueryRq").Single().Attribute("iterator")!.Value);
    }

    [Theory] [InlineData(null)] [InlineData("line1")]
    public void DuplicateCompositeIdentityStillFails(string? line)
    {
        var processor = new Processor([Page("TransactionQuery", 1, Transaction(line)), Page("TransactionQuery", 0, Transaction(line))]);
        using var reader = new QuickBooksReader(processor, Company);
        Assert.Equal("incomplete_response", Assert.Throws<QbException>(() => reader.QuerySdkAll(Plan(), Company, 1)).Code);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ProjectedFieldsGetInternalIdentityChecksWithoutLeakingAddedFields(bool includeTxnId)
    {
        var fields = new JsonObject { ["IncludeRetElement"] = includeTxnId ? new JsonArray("Amount", "TxnID") : JsonValue.Create("Amount") };
        var processor = new Processor([Page("TransactionQuery", 1, Transaction(null, "15.00")),
            Page("TransactionQuery", 0, Transaction("cashback", "5.00"))]);
        using var reader = new QuickBooksReader(processor, Company);
        var result = reader.QuerySdkAll(Plan(fields), Company, 1);
        Assert.Equal(2, result.Data.Count);
        foreach (var request in processor.Requests)
            Assert.Equal(new[] { "Amount", "TxnID", "TxnType", "TxnLineID", "AccountRef" }.Order(),
                request.Descendants("IncludeRetElement").Select(e => e.Value).Order());
        Assert.All(result.Data, record => Assert.Equal(includeTxnId ? new[] { "TxnID", "Amount" } : ["Amount"], record.Children.Select(c => c.Name)));
        Assert.Equal(includeTxnId ? 2 : 1, fields["IncludeRetElement"] is JsonArray array ? array.Count : 1);
    }

    [Fact] public void EmptyProjectionKeepsSdkDefaultFullRecords()
    {
        var processor = new Processor([Page("TransactionQuery", 0, Transaction("line1"))]);
        using var reader = new QuickBooksReader(processor, Company);
        var result = reader.QuerySdkAll(Plan(new() { ["IncludeRetElement"] = new JsonArray() }), Company, 1);
        Assert.Empty(processor.Requests[0].Descendants("IncludeRetElement"));
        Assert.Contains(result.Data[0].Children, c => c.Name == "TxnLineID");
    }

    [Fact] public void MissingTransactionIdentityFailsRatherThanClaimingComplete()
    {
        using var reader = new QuickBooksReader(new Processor([Page("TransactionQuery", 0, Transaction(null, identity: false))]), Company);
        Assert.Equal("incomplete_response", Assert.Throws<QbException>(() => reader.QuerySdkAll(Plan(), Company, 1)).Code);
    }

    [Fact] public void InvalidProjectionTypeFailsBeforeSendingAnSdkQuery()
    {
        var processor = new Processor([]);
        using var reader = new QuickBooksReader(processor, Company);
        Assert.Equal("invalid_arguments", Assert.Throws<QbException>(() => reader.QuerySdkAll(Plan(new() { ["IncludeRetElement"] = 42 }), Company, 1)).Code);
        Assert.Empty(processor.Requests);
    }

    [Fact] public void OrdinaryListQueriesStillRejectDuplicateListIds()
    {
        XElement Record() => new("CustomerRet", new XElement("ListID", "same-customer"));
        var processor = new Processor([Page("CustomerQuery", 1, Record()), Page("CustomerQuery", 0, Record())]);
        using var reader = new QuickBooksReader(processor, Company);
        Assert.Equal("incomplete_response", Assert.Throws<QbException>(() => reader.QuerySdkAll(Plan(operation: "CustomerQuery"), Company, 1)).Code);
    }

    private sealed class Processor(IEnumerable<string> pages) : IRequestProcessor
    {
        private readonly Queue<string> pages = new(pages);
        public List<XDocument> Requests { get; } = [];
        public void Open() { }
        public void ConfigureAccess() { }
        public string Begin(string companyFile) => "ticket";
        public string CurrentCompanyFile(string ticket) => Company;
        public string Process(string ticket, string request)
        {
            var document = XDocument.Parse(request);
            if (document.Descendants("HostQueryRq").Any()) return "<QBXML><QBXMLMsgsRs><HostQueryRs requestID='host' statusCode='0' statusSeverity='Info'><HostRet><ProductName>Test host</ProductName><MajorVersion>34</MajorVersion><MinorVersion>0</MinorVersion><Country>US</Country><SupportedQBXMLVersion>13.0</SupportedQBXMLVersion></HostRet></HostQueryRs><CompanyQueryRs requestID='company' statusCode='0' statusSeverity='Info'><CompanyRet><CompanyName>Test</CompanyName></CompanyRet></CompanyQueryRs></QBXMLMsgsRs></QBXML>";
            Requests.Add(document);
            return pages.Dequeue();
        }
        public void End(string ticket) { }
        public void Close() { }
        public void Dispose() { }
    }
}
