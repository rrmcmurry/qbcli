using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class DocumentTests
{
    private const string Company = @"C:\Test.QBW";
    private static SdkElement Element(string name, string? value = null, params SdkElement[] children) => new(name, new Dictionary<string, string>(), value, children);
    private static SdkElement Record(DocumentKind kind, string? amount = "12.34") => Element(DocumentQueries.Name(kind) + "Ret", null,
        Element("TxnID", "txn"), Element("TxnDate", "2026-10-05"), Element("RefNumber", "123"),
        Element(kind == DocumentKind.Invoice ? "CustomerRef" : "VendorRef", null, Element("ListID", "party"), Element("FullName", "Test & Job")),
        Element(kind == DocumentKind.Invoice ? "BalanceRemaining" : "OpenAmount", amount), Element("IsPaid", "false"));

    [Theory] [InlineData(DocumentKind.Invoice)] [InlineData(DocumentKind.Bill)]
    public void OpenAndAllPlansUseSdkPaidFilterWithoutMutation(DocumentKind kind)
    {
        foreach (var open in new[] { false, true })
        {
            var prepared = SdkBatch.Prepare(DocumentQueries.Plan(kind, open, "US"));
            Assert.False(prepared.RequiresApply);
            Assert.Equal(open ? "NotPaidOnly" : "All", prepared.Requests[0].Element("PaidStatus")!.Value);
            Assert.Equal("false", prepared.Requests[0].Element("IncludeLineItems")!.Value);
            Assert.Equal("false", prepared.Requests[0].Element("IncludeLinkedTxns")!.Value);
        }
    }

    [Theory] [InlineData(DocumentKind.Invoice)] [InlineData(DocumentKind.Bill)]
    public void SummaryUsesInvariantAmountsAndPartyIdentity(DocumentKind kind)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var record = DocumentQueries.Normalize(Record(kind), kind, true,kind==DocumentKind.Bill?12.34m:null);
            Assert.Equal(12.34m, record.RemainingAmount);
            Assert.Equal(new DocumentParty("party", "Test & Job"), record.Party);
            Assert.False(record.Paid);
            Assert.Null(record.Currency);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact] public void MissingBillOpenAmountNeverUsesOriginalAmountDue()
    {
        var record = Record(DocumentKind.Bill);
        record = record with { Children = record.Children.Where(c => c.Name != "OpenAmount").Append(Element("AmountDue", "999.99")).ToArray() };
        Assert.Null(DocumentQueries.Normalize(record, DocumentKind.Bill, true).RemainingAmount);
    }
    [Fact] public void VendorAggregateOpenAmountIsNeverTreatedAsBillBalance()
    {
        var record=Record(DocumentKind.Bill,"203.80");
        Assert.Null(DocumentQueries.Normalize(record,DocumentKind.Bill,true).RemainingAmount);
        Assert.Equal(70.25m,DocumentQueries.Normalize(record,DocumentKind.Bill,true,70.25m).RemainingAmount);
        record=record with {Children=record.Children.Select(c=>c.Name=="IsPaid"?c with {Value="true"}:c).ToArray()};
        Assert.Equal(0m,DocumentQueries.Normalize(record,DocumentKind.Bill,false).RemainingAmount);
    }
    [Fact] public void OutstandingBillRequestsAreDeduplicatedByVendorAndSkipPaidDocuments()
    {
        var unpaid=DocumentQueries.Normalize(Record(DocumentKind.Bill),DocumentKind.Bill,false);
        var paid=unpaid with {Paid=true,Party=new("other","Other")};
        var plan=DocumentQueries.BillBalancesPlan([unpaid,unpaid with {Id="second"},paid],"US");
        Assert.Single(plan.Requests);Assert.Equal("party",plan.Requests[0].Fields["PayeeEntityRef"]!["ListID"]!.GetValue<string>());
        Assert.False(SdkBatch.Prepare(plan).RequiresApply);
    }
    [Theory] [InlineData("missing")] [InlineData("duplicate")] [InlineData("malformed")] [InlineData("warning")]
    public void IncompleteBillBalanceEvidenceFailsInsteadOfPublishingPartialAmounts(string problem)
    {
        var detail=Element("BillToPay",null,Element("TxnID","txn"),Element("TxnType","Bill"),Element("AmountDue","70.25"));
        if(problem=="missing")detail=detail with {Children=detail.Children.Where(c=>c.Name!="AmountDue").ToArray()};
        if(problem=="malformed")detail=detail with {Children=detail.Children.Select(c=>c.Name=="AmountDue"?c with {Value="NaN"}:c).ToArray()};
        var row=Element("BillToPayRet",null,detail);
        var execution=new SdkExecutionResult(problem!="warning",1,Company,"",false,false,null,
            [new("b","BillToPayQuery",problem=="warning"?"warning":"succeeded",0,"Info",null,null,problem=="duplicate"?[row,row]:[row])]);
        Assert.Throws<QbException>(()=>DocumentQueries.BillBalances(execution));
    }

    [Theory] [InlineData("1,234.50")] [InlineData("1e2")] [InlineData("NaN")] [InlineData("")]
    public void InvalidAmountsAreRejected(string amount) => Assert.Throws<QbException>(() => DocumentQueries.Normalize(Record(DocumentKind.Invoice, amount), DocumentKind.Invoice, false));

    [Theory] [InlineData("TxnID")] [InlineData("TxnDate")] [InlineData("CustomerRef")]
    public void MissingRequiredIdentityFails(string field)
    {
        var record = Record(DocumentKind.Invoice);
        record = record with { Children = record.Children.Where(c => c.Name != field).ToArray() };
        Assert.Equal("invalid_response", Assert.Throws<QbException>(() => DocumentQueries.Normalize(record, DocumentKind.Invoice, false)).Code);
    }

    [Theory] [InlineData("2026-02-30")] [InlineData("10/05/2026")] [InlineData("2026-10-05T00:00:00")]
    public void InvalidDatesFail(string value)
    {
        var record = Record(DocumentKind.Invoice);
        record = record with { Children = record.Children.Select(c => c.Name == "TxnDate" ? c with { Value = value } : c).ToArray() };
        Assert.Throws<QbException>(() => DocumentQueries.Normalize(record, DocumentKind.Invoice, false));
    }

    [Fact] public void DuplicateSummaryFieldsAndWrongRecordTypesFail()
    {
        var record = Record(DocumentKind.Invoice);
        Assert.Throws<QbException>(() => DocumentQueries.Normalize(record with { Children = [..record.Children, Element("BalanceRemaining", "5")] }, DocumentKind.Invoice, false));
        Assert.Throws<QbException>(() => DocumentQueries.Normalize(record, DocumentKind.Bill, false));
    }

    [Fact] public void OptionalMissingFieldsStayUnknownAndCurrencyIsExplicit()
    {
        var record = Record(DocumentKind.Invoice);
        record = record with { Children = record.Children.Where(c => c.Name is not ("BalanceRemaining" or "IsPaid")).Append(
            Element("CurrencyRef", null, Element("ListID", "currency"), Element("FullName", "Euro"))).ToArray() };
        var result = DocumentQueries.Normalize(record, DocumentKind.Invoice, true);
        Assert.Null(result.RemainingAmount); Assert.Null(result.Paid);
        Assert.Equal(new DocumentCurrency("currency", "Euro"), result.Currency);
    }

    [Fact] public void OpenFilterRejectsUnexpectedPaidOrZeroBalanceRecords()
    {
        Assert.Throws<QbException>(() => DocumentQueries.Normalize(Record(DocumentKind.Invoice, "0"), DocumentKind.Invoice, true));
        var record = Record(DocumentKind.Invoice);
        record = record with { Children = record.Children.Select(c => c.Name == "IsPaid" ? c with { Value = "true" } : c).ToArray() };
        Assert.Throws<QbException>(() => DocumentQueries.Normalize(record, DocumentKind.Invoice, true));
        Assert.True(DocumentQueries.Normalize(record, DocumentKind.Invoice, false).Paid);
    }

    [Theory] [InlineData("invoices")] [InlineData("bills")]
    public void CliOptionsAcceptOpenAndPagination(string name)
    {
        var options = Options.Parse([name, "list", "--open", "--page-size", "1"]);
        Assert.True(options.OpenOnly); Assert.Equal(1, options.PageSize);
        Assert.Throws<QbException>(() => Options.Parse([name, "list", "--open", "--open"]));
        Assert.Throws<QbException>(() => Options.Parse([name, "list", "--apply"]));
    }

    [Theory] [InlineData("accounts", "list")] [InlineData("customers", "list")] [InlineData("capabilities", "--live")]
    public void OtherCommandsRejectOpen(string first, string second) => Assert.Throws<QbException>(() => Options.Parse([first, second, "--open"]));

    [Theory] [InlineData(DocumentKind.Invoice)] [InlineData(DocumentKind.Bill)]
    public void AdapterReadsAllPagesNormalizesSortsAndCloses(DocumentKind kind)
    {
        var processor = new Processor(kind);
        using (var reader = new QuickBooksReader(processor, Company))
        {
            var result = reader.ReadDocuments(kind, true, 1, Company);
            Assert.True(result.Complete); Assert.Equal(2, result.Pages);
            Assert.Equal(new[] { "earlier", "later" }, result.Results.Select(r => r.Id));
            Assert.All(result.Results, r => Assert.Equal(12.34m, r.RemainingAmount));
            Assert.All(processor.Requests.Where(r=>r.Descendants("PaidStatus").Any()), r => Assert.Equal("NotPaidOnly", r.Descendants("PaidStatus").Single().Value));
            Assert.Equal(kind==DocumentKind.Bill?1:0,processor.Requests.Count(r=>r.Descendants("BillToPayQueryRq").Any()));
            Assert.Equal("Start", processor.Requests[0].Descendants(kind + "QueryRq").Single().Attribute("iterator")!.Value);
            Assert.Equal("Continue", processor.Requests[1].Descendants(kind + "QueryRq").Single().Attribute("iterator")!.Value);
        }
        Assert.True(processor.Closed);
    }

    [Fact] public void CompanyMismatchFailsBeforeReadingDocuments()
    {
        var processor = new Processor(DocumentKind.Invoice);
        using var reader = new QuickBooksReader(processor, Company);
        Assert.Equal("company_mismatch", Assert.Throws<QbException>(() => reader.ReadDocuments(DocumentKind.Invoice, true, 1, @"C:\Other.QBW")).Code);
        Assert.Empty(processor.Requests);
    }

    [Fact] public void ConsoleDoesNotAssumeCurrencyOrHideMissingBalance()
    {
        var record = DocumentQueries.Normalize(Record(DocumentKind.Bill) with { Children = Record(DocumentKind.Bill).Children.Where(c => c.Name != "OpenAmount").ToArray() }, DocumentKind.Bill, true);
        var writer = new StringWriter();
        Output.Write(writer, new DocumentsResult(true, 1, "Test", Company, "bill", true, true, 1, [record]), false);
        Assert.Contains("Unknown  Unknown", writer.ToString());
        Assert.Contains("complete: True", writer.ToString());
    }

    [Theory] [InlineData(DocumentKind.Invoice)] [InlineData(DocumentKind.Bill)]
    public void CliReturnsNormalizedJsonAndDisposesSession(DocumentKind kind)
    {
        var processor = new Processor(kind);
        var output = new StringWriter();
        var command = kind == DocumentKind.Invoice ? "invoices" : "bills";
        Assert.Equal(0, CliApplication.Run([command, "list", "--open", "--page-size", "1", "--company-file", Company, "--json"],
            (options, _) => { Assert.False(options.Apply); return new QuickBooksReader(processor, options.CompanyFile); }, output, new StringWriter()));
        var json = JsonNode.Parse(output.ToString())!;
        Assert.True(json["complete"]!.GetValue<bool>());
        Assert.Equal(2, json["results"]!.AsArray().Count);
        Assert.Equal("earlier", json["results"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("Test & Job", json["results"]![0]!["party"]!["fullName"]!.GetValue<string>());
        Assert.DoesNotContain("BalanceRemaining", output.ToString());
        Assert.DoesNotContain("QueryRs", output.ToString());
        Assert.True(processor.Closed);
    }

    [Fact] public void EmptyUnpaidQueryReturnsCompleteEmptyArray()
    {
        var processor = new Processor(DocumentKind.Invoice) { Empty = true };
        using var reader = new QuickBooksReader(processor, Company);
        var result = reader.ReadDocuments(DocumentKind.Invoice, true, 1, Company);
        Assert.True(result.Complete); Assert.Empty(result.Results); Assert.Equal(1, result.Pages);
    }

    [Fact] public void FailureOnLaterPageNeverEmitsEarlierRecordsAsSuccess()
    {
        var processor = new Processor(DocumentKind.Invoice) { FailSecond = true };
        var output = new StringWriter();
        Assert.Equal(4, CliApplication.Run(["invoices", "list", "--open", "--page-size", "1", "--json"],
            (_, _) => new QuickBooksReader(processor, Company), output, new StringWriter()));
        var error = JsonNode.Parse(output.ToString())!;
        Assert.False(error["success"]!.GetValue<bool>());
        Assert.Null(error["results"]);
        Assert.True(processor.Closed);
    }

    private sealed class Processor(DocumentKind kind) : IRequestProcessor
    {
        public List<XDocument> Requests { get; } = [];
        public bool Closed;
        public bool Empty, FailSecond;
        public void Open() { }
        public void ConfigureAccess() { }
        public string Begin(string companyFile) => "ticket";
        public string CurrentCompanyFile(string ticket) => Company;
        public string Process(string ticket, string request)
        {
            var xml = XDocument.Parse(request);
            if (xml.Descendants("HostQueryRq").Any()) return "<QBXML><QBXMLMsgsRs><HostQueryRs requestID='host' statusCode='0' statusSeverity='Info'><HostRet><ProductName>Test</ProductName><MajorVersion>34</MajorVersion><MinorVersion>0</MinorVersion><Country>US</Country><SupportedQBXMLVersion>13.0</SupportedQBXMLVersion></HostRet></HostQueryRs><CompanyQueryRs requestID='company' statusCode='0' statusSeverity='Info'><CompanyRet><CompanyName>Test</CompanyName></CompanyRet></CompanyQueryRs></QBXMLMsgsRs></QBXML>";
            Requests.Add(xml);
            if(xml.Descendants("BillToPayQueryRq").Any())
                return new XElement("QBXML",new XElement("QBXMLMsgsRs",new XElement("BillToPayQueryRs",
                    new XAttribute("requestID",xml.Descendants("BillToPayQueryRq").Single().Attribute("requestID")!.Value),
                    new XAttribute("statusCode","0"),new XAttribute("statusSeverity","Info"),
                    new[]{"later","earlier"}.Select(id=>new XElement("BillToPayRet",new XElement("BillToPay",
                        new XElement("TxnID",id),new XElement("TxnType","Bill"),new XElement("AmountDue","12.34"))))))).ToString();
            if (Empty || FailSecond && Requests.Count == 2)
                return $"<QBXML><QBXMLMsgsRs><{kind}QueryRs requestID='documents' statusCode='{(Empty ? 1 : 500)}' statusSeverity='{(Empty ? "Info" : "Error")}' iteratorRemainingCount='0'/></QBXMLMsgsRs></QBXML>";
            var record = Record(kind);
            var node = new XElement(record.Name, record.Children.Select(ToXml));
            node.Element("TxnID")!.Value = Requests.Count == 1 ? "later" : "earlier";
            node.Element("TxnDate")!.Value = Requests.Count == 1 ? "2026-10-05" : "2026-10-04";
            return new XElement("QBXML", new XElement("QBXMLMsgsRs", new XElement(kind + "QueryRs",
                new XAttribute("requestID", "documents"), new XAttribute("statusCode", "0"), new XAttribute("statusSeverity", "Info"),
                new XAttribute("iteratorRemainingCount", Requests.Count == 1 ? "1" : "0"),
                new XAttribute("iteratorID", "{11111111-1111-1111-1111-111111111111}"), node))).ToString();
        }
        private static XElement ToXml(SdkElement e) => new(e.Name, e.Value is null ? e.Children.Select(ToXml) : e.Value);
        public void End(string ticket) { }
        public void Close() => Closed = true;
        public void Dispose() { }
    }
}
