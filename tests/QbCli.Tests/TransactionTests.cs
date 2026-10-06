using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class TransactionTests
{
    private static SdkElement Node(XElement xml) => new(xml.Name.LocalName, xml.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value),
        xml.HasElements ? null : xml.Value, xml.Elements().Select(Node).ToArray());
    private static XElement Record(string id = "txn", string? memo = "insurance", string type = "Check", string date = "2026-10-05") =>
        new("TransactionRet", new XElement("TxnID", id), new XElement("TxnType", type), new XElement("TxnDate", date),
            new XElement("Amount", "-12.34"), memo is null ? null : new XElement("Memo", memo));

    [Fact] public void PlanUsesInclusiveTransactionDatesAndSummaryOnly()
    {
        var selection = new TransactionSelection("2026-01-01", "2026-12-31", "insurance");
        var prepared = SdkBatch.Prepare(TransactionQueries.Plan(selection, "US"));
        Assert.False(prepared.RequiresApply);
        var fields = prepared.Requests.Single();
        Assert.Equal("SummaryOnly", fields.Element("TransactionDetailLevelFilter")!.Value);
        Assert.Equal("2026-01-01", fields.Element("TransactionDateRangeFilter")!.Element("FromTxnDate")!.Value);
        Assert.Equal("2026-12-31", fields.Element("TransactionDateRangeFilter")!.Element("ToTxnDate")!.Value);
        Assert.Empty(fields.Descendants("Memo")); Assert.Empty(fields.Descendants("MaxReturned"));
    }

    [Fact] public void UnboundedSelectionNeverInventsDateCutoffs()
    {
        Assert.Null(SdkBatch.Prepare(TransactionQueries.Plan(new(), "US")).Requests.Single().Element("TransactionDateRangeFilter"));
        var query = SdkBatch.Prepare(TransactionQueries.Plan(new(Until: "2026-10-05"), "US")).Requests.Single();
        Assert.Null(query.Element("TransactionDateRangeFilter")!.Element("FromTxnDate"));
    }

    [Fact] public void SignedAmountsAreInvariantAndUnknownFieldsStayNull()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var transaction = TransactionQueries.Normalize(Node(Record()));
            Assert.Equal(-12.34m, transaction.Amount);
            Assert.Null(transaction.Account); Assert.Null(transaction.Entity); Assert.Null(transaction.Currency); Assert.Null(transaction.Number);
            var xml = Record(); xml.Element("Amount")!.Remove();
            Assert.Null(TransactionQueries.Normalize(Node(xml)).Amount);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact] public void ExplicitReferencesAndCurrencyArePreserved()
    {
        var xml = Record(); xml.Add(new XElement("CurrencyRef", new XElement("ListID", "currency"), new XElement("FullName", "Euro")),
            new XElement("AccountRef", new XElement("ListID", "account"), new XElement("FullName", "Bank")));
        var transaction = TransactionQueries.Normalize(Node(xml));
        Assert.Equal(new TransactionReference("currency", "Euro"), transaction.Currency);
        Assert.Equal(new TransactionReference("account", "Bank"), transaction.Account);
    }

    [Theory] [InlineData("TxnID")] [InlineData("TxnType")] [InlineData("TxnDate")]
    public void MissingSummaryIdentityFails(string name)
    {
        var xml = Record(); xml.Element(name)!.Remove();
        Assert.Equal("invalid_response", Assert.Throws<QbException>(() => TransactionQueries.Normalize(Node(xml))).Code);
    }

    [Theory] [InlineData("TxnID")] [InlineData("Amount")] [InlineData("Memo")]
    public void DuplicateFieldsFail(string name)
    {
        var xml = Record(); xml.Add(new XElement(xml.Element(name)!)); Assert.Throws<QbException>(() => TransactionQueries.Normalize(Node(xml)));
    }

    [Theory] [InlineData("1e2")] [InlineData("1,234.50")] [InlineData("")]
    public void InvalidAmountsFail(string value)
    {
        var xml = Record(); xml.Element("Amount")!.Value = value; Assert.Throws<QbException>(() => TransactionQueries.Normalize(Node(xml)));
    }

    [Fact] public void PostingLineAndInvalidDateNeverBecomeValidSummary()
    {
        var xml = Record(); xml.Add(new XElement("TxnLineID", "line")); Assert.Throws<QbException>(() => TransactionQueries.Normalize(Node(xml)));
        Assert.Throws<QbException>(() => TransactionQueries.Normalize(Node(Record(date: "2026-02-30"))));
    }

    [Fact] public void MemoSearchUsesOrdinalCaseInsensitiveSubstringAfterNormalization()
    {
        var records = new[] { Record("z", "INSURANCE policy"), Record("a", "insurance", "Bill"), Record("n", null), Record("x", "other") }
            .Select(r => TransactionQueries.Normalize(Node(r)));
        var result = TransactionQueries.Select(records, new(MemoContains: "insurance"));
        Assert.Equal(new[] { "a", "z" }, result.Select(r => r.Id));
    }

    [Theory]
    [InlineData("transactions list --since 2026-01-01 --until 2026-12-31 --page-size 1")]
    [InlineData("transactions search --memo insurance")]
    public void ValidOptionsAreAccepted(string args) => Assert.NotNull(Options.Parse(args.Split(' ')).Transactions);

    [Theory]
    [InlineData("transactions search")]
    [InlineData("transactions list --memo insurance")]
    [InlineData("transactions list --since 2026-02-30")]
    [InlineData("transactions list --since 2026-12-31 --until 2026-01-01")]
    [InlineData("transactions list --since 2026-01-01 --since 2026-02-01")]
    [InlineData("transactions search --memo insurance --memo other")]
    [InlineData("transactions list --open")]
    [InlineData("transactions list --apply")]
    [InlineData("transactions list --from 2026-01-01")]
    [InlineData("company info --memo insurance")]
    public void InvalidOptionsFailBeforeConnection(string args)
    {
        Assert.Equal(2, CliApplication.Run([.. args.Split(' '), "--json"], (_, _) => throw new Exception("Must not connect"), new StringWriter(), new StringWriter()));
    }

    [Fact] public void LaterPageMemoMatchIsNotLostAndScannedCountIsAccurate()
    {
        var processor = new Processor(); var output = new StringWriter();
        Assert.Equal(0, CliApplication.Run(["transactions", "search", "--memo", "insurance", "--page-size", "1", "--json"],
            (_, _) => new QuickBooksReader(processor, @"C:\Test.QBW"), output, new StringWriter()));
        var result = JsonNode.Parse(output.ToString())!;
        Assert.Equal(2, result["scannedRecords"]!.GetValue<int>()); Assert.Equal(2, result["pages"]!.GetValue<int>());
        Assert.Single(result["results"]!.AsArray()); Assert.Equal("second", result["results"]![0]!["id"]!.GetValue<string>());
        Assert.True(result["complete"]!.GetValue<bool>()); Assert.True(processor.Closed);
        Assert.DoesNotContain("TransactionRet", output.ToString());
    }

    [Fact] public void LaterPageFailureCannotBecomeSuccessfulMemoSearch()
    {
        var processor = new Processor { FailSecond = true }; var output = new StringWriter();
        Assert.Equal(4, CliApplication.Run(["transactions", "search", "--memo", "insurance", "--page-size", "1", "--json"],
            (_, _) => new QuickBooksReader(processor, @"C:\Test.QBW"), output, new StringWriter()));
        Assert.Null(JsonNode.Parse(output.ToString())!["results"]); Assert.True(processor.Closed);
    }

    [Fact] public void SdkDateFilterViolationFailsInsteadOfSilentlyDroppingRecords()
    {
        using var reader = new QuickBooksReader(new Processor(), @"C:\Test.QBW");
        Assert.Equal("invalid_response", Assert.Throws<QbException>(() => reader.ReadTransactions(new(Since: "2026-12-01"), 1, "")).Code);
    }

    [Fact] public void CompanyMismatchPreventsReadingTransactionData()
    {
        var processor = new Processor(); using var reader = new QuickBooksReader(processor, @"C:\Test.QBW");
        Assert.Equal("company_mismatch", Assert.Throws<QbException>(() => reader.ReadTransactions(new(), 1, @"C:\Other.QBW")).Code);
        Assert.Equal(0, processor.Pages);
    }

    [Fact] public void ConsoleShowsUnknownAmountCurrencyAndNeutralizesMemoControls()
    {
        var transaction = TransactionQueries.Normalize(Node(Record(memo: "line\ncontrol"))) with { Amount = null };
        var output = new StringWriter();
        Output.Write(output, new TransactionsResult(true, 1, "Test", @"C:\Test.QBW", new(), true, 1, 1, TransactionQueries.Coverage, [transaction]), false);
        Assert.Contains("Unknown  Unknown", output.ToString());
        Assert.Contains("line control", output.ToString());
    }

    private sealed class Processor : IRequestProcessor
    {
        public int Pages; public bool Closed, FailSecond;
        public void Open() { }
        public void ConfigureAccess() { }
        public string Begin(string file) => "ticket";
        public string CurrentCompanyFile(string ticket) => @"C:\Test.QBW";
        public string Process(string ticket, string request)
        {
            var xml = XDocument.Parse(request);
            if (xml.Descendants("HostQueryRq").Any()) return "<QBXML><QBXMLMsgsRs><HostQueryRs requestID='host' statusCode='0' statusSeverity='Info'><HostRet><ProductName>Test</ProductName><MajorVersion>34</MajorVersion><MinorVersion>0</MinorVersion><Country>US</Country><SupportedQBXMLVersion>13.0</SupportedQBXMLVersion></HostRet></HostQueryRs><CompanyQueryRs requestID='company' statusCode='0' statusSeverity='Info'><CompanyRet><CompanyName>Test</CompanyName></CompanyRet></CompanyQueryRs></QBXMLMsgsRs></QBXML>";
            Pages++;
            Assert.Equal("SummaryOnly", xml.Descendants("TransactionDetailLevelFilter").Single().Value);
            if (FailSecond && Pages == 2) return "<QBXML><QBXMLMsgsRs><TransactionQueryRs requestID='history' statusCode='500' statusSeverity='Error'/></QBXMLMsgsRs></QBXML>";
            return new XElement("QBXML", new XElement("QBXMLMsgsRs", new XElement("TransactionQueryRs",
                new XAttribute("requestID", "history"), new XAttribute("statusCode", "0"), new XAttribute("statusSeverity", "Info"),
                new XAttribute("iteratorRemainingCount", Pages == 1 ? "1" : "0"), new XAttribute("iteratorID", "{11111111-1111-1111-1111-111111111111}"),
                Record(Pages == 1 ? "first" : "second", Pages == 1 ? "unrelated" : "INSURANCE premium")))).ToString();
        }
        public void End(string ticket) { }
        public void Close() => Closed = true;
        public void Dispose() { }
    }
}
