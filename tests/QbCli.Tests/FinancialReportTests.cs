using System.Xml.Linq;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class FinancialReportTests
{
    private static ReportSelection Selection(FinancialReportKind kind = FinancialReportKind.TrialBalance, string basis = "accrual") =>
        kind == FinancialReportKind.ProfitLoss ? new(kind, "2026-01-01", "2026-12-31", null, basis) : new(kind, null, null, "2026-12-31", basis);
    private static SdkElement Node(XElement xml) => new(xml.Name.LocalName, xml.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value),
        xml.HasElements ? null : xml.Value, xml.Elements().Select(Node).ToArray());
    private static XElement Report() => XElement.Parse("""
        <ReportRet><ReportTitle>Trial Balance</ReportTitle><ReportSubtitle>As of December 31, 2026</ReportSubtitle><ReportBasis>Accrual</ReportBasis>
        <NumRows>5</NumRows><NumColumns>3</NumColumns><NumColTitleRows>2</NumColTitleRows>
        <ColDesc colID="1" dataType="STRTYPE"><ColTitle titleRow="1"/><ColTitle titleRow="2"/><ColType>Label</ColType></ColDesc>
        <ColDesc colID="2" dataType="AMTTYPE"><ColTitle titleRow="1" value="Dec 31, 26"/><ColTitle titleRow="2" value="Debit"/><ColType>Amount</ColType></ColDesc>
        <ColDesc colID="3" dataType="AMTTYPE"><ColTitle titleRow="1" value="Dec 31, 26"/><ColTitle titleRow="2" value="Credit"/><ColType>Amount</ColType></ColDesc>
        <ReportData>
        <TextRow rowNumber="1" value="ASSETS"/>
        <DataRow rowNumber="2"><RowData rowType="account" value="Checking"/><ColData colID="1" value="Checking"/><ColData colID="2" value="-12.34"/></DataRow>
        <DataRow rowNumber="3"><RowData rowType="account" value="Other"/><ColData colID="1" value="Other"/><ColData colID="2" value=""/><ColData colID="3" value="0.00"/></DataRow>
        <SubtotalRow rowNumber="4"><ColData colID="1" value="Total Assets"/><ColData colID="2" value="-12.34"/></SubtotalRow>
        <TotalRow rowNumber="5"><ColData colID="1" value="TOTAL"/><ColData colID="2" value="-12.34"/><ColData colID="3"/></TotalRow>
        </ReportData></ReportRet>
        """);
    private static FinancialReportResult Normalize(XElement? report = null) => FinancialReports.Normalize(Node(report ?? Report()), Selection(), "Test", @"C:\Test.QBW");

    [Theory]
    [InlineData(FinancialReportKind.TrialBalance, "TrialBalance")]
    [InlineData(FinancialReportKind.ProfitLoss, "ProfitAndLossStandard")]
    [InlineData(FinancialReportKind.BalanceSheet, "BalanceSheetStandard")]
    public void PlansAreReadOnlyWithExplicitDatesAndNoDisplay(FinancialReportKind kind, string type)
    {
        var prepared = SdkBatch.Prepare(FinancialReports.Plan(Selection(kind, "cash"), "US"));
        Assert.False(prepared.RequiresApply);
        var query = prepared.Requests.Single();
        Assert.Equal(type, query.Element("GeneralSummaryReportType")!.Value);
        Assert.Equal("false", query.Element("DisplayReport")!.Value);
        Assert.Equal("Cash", query.Element("ReportBasis")!.Value);
        Assert.Equal("All", query.Element("ReturnRows")!.Value);
        Assert.Equal("All", query.Element("ReturnColumns")!.Value);
        Assert.Equal("2026-12-31", query.Element("ReportPeriod")!.Element("ToReportDate")!.Value);
        Assert.Equal(kind == FinancialReportKind.ProfitLoss, query.Element("ReportPeriod")!.Element("FromReportDate") is not null);
    }

    [Fact] public void PreserveRowKindsSparseValuesAndMultiLineTitles()
    {
        var report = Normalize();
        Assert.True(report.Complete);
        Assert.Equal(new[] { "text", "data", "data", "subtotal", "total" }, report.Rows.Select(r => r.Kind));
        Assert.Equal("ASSETS", report.Rows[0].Text);
        Assert.Equal("account", report.Rows[1].EntityType);
        Assert.Equal("Checking", report.Rows[1].EntityName);
        Assert.Equal(new[] { "Dec 31, 26", "Debit" }, report.Columns[1].Titles.Select(t => t.Text));
        Assert.Equal(-12.34m, report.Rows[1].Cells[1].Amount);
        Assert.DoesNotContain(report.Rows[1].Cells, c => c.ColumnId == 3);
        Assert.Equal("", report.Rows[2].Cells[1].Text); Assert.Null(report.Rows[2].Cells[1].Amount);
        Assert.Equal(0m, report.Rows[2].Cells[2].Amount);
        Assert.Null(report.Rows[4].Cells[2].Text); Assert.Null(report.Rows[4].Cells[2].Amount);
    }

    [Fact] public void CellTypeOverrideDoesNotBecomeMoney()
    {
        var xml = Report();
        xml.Descendants("ColData").First(c => c.Attribute("colID")!.Value == "2").SetAttributeValue("dataType", "QUANTYPE");
        var cell = Normalize(xml).Rows[1].Cells[1];
        Assert.Equal("quantity", cell.ValueType); Assert.Null(cell.Amount); Assert.Equal("-12.34", cell.Text);
    }

    [Theory] [InlineData("NumRows", "6")] [InlineData("NumColumns", "4")] [InlineData("NumColTitleRows", "1")]
    public void InconsistentDeclaredDimensionsFail(string field, string value)
    {
        var xml = Report(); xml.Element(field)!.Value = value;
        Assert.Throws<QbException>(() => Normalize(xml));
    }

    [Theory] [InlineData("NumRows")] [InlineData("NumColumns")] [InlineData("NumColTitleRows")]
    public void MissingRequiredDimensionsFail(string field)
    {
        var xml = Report(); xml.Element(field)!.Remove(); Assert.Throws<QbException>(() => Normalize(xml));
    }

    [Fact] public void DuplicateRowsColumnsCellsAndTitleRowsFail()
    {
        var row = Report(); row.Element("ReportData")!.Elements().ElementAt(1).SetAttributeValue("rowNumber", "1");
        Assert.Throws<QbException>(() => Normalize(row));
        var col = Report(); col.Elements("ColDesc").ElementAt(1).SetAttributeValue("colID", "1");
        Assert.Throws<QbException>(() => Normalize(col));
        var cell = Report(); cell.Element("ReportData")!.Elements().ElementAt(1).Add(new XElement("ColData", new XAttribute("colID", "2"), new XAttribute("value", "1")));
        Assert.Throws<QbException>(() => Normalize(cell));
        var title = Report(); title.Elements("ColDesc").First().Elements("ColTitle").Last().SetAttributeValue("titleRow", "1");
        Assert.Throws<QbException>(() => Normalize(title));
    }

    [Fact] public void UnknownColumnReferencesAndRowTypesFail()
    {
        var col = Report(); col.Descendants("ColData").First().SetAttributeValue("colID", "99"); Assert.Throws<QbException>(() => Normalize(col));
        var row = Report(); row.Element("ReportData")!.Elements().First().Name = "UnknownRow"; Assert.Throws<QbException>(() => Normalize(row));
    }

    [Theory] [InlineData("1,234.50")] [InlineData("NaN")] [InlineData("1e2")]
    public void InvalidAmountsFail(string value)
    {
        var xml = Report(); xml.Descendants("ColData").First(c => c.Attribute("colID")!.Value == "2").SetAttributeValue("value", value);
        Assert.Throws<QbException>(() => Normalize(xml));
    }

    [Fact] public void EmptyReportCanOmitReportData()
    {
        var xml = Report(); xml.Element("NumRows")!.Value = "0"; xml.Element("ReportData")!.Remove();
        Assert.True(Normalize(xml).Complete); Assert.Empty(Normalize(xml).Rows);
    }

    [Fact] public void RenderUsesDeclaredColumnsAndRetainsHeadings()
    {
        var writer = new StringWriter(); Output.Write(writer, Normalize(), false);
        Assert.Contains("Debit", writer.ToString()); Assert.Contains("Credit", writer.ToString()); Assert.Contains("ASSETS", writer.ToString());
        Assert.Contains("Checking  |  -12.34  |", writer.ToString());
    }

    [Theory]
    [InlineData("reports trial-balance --as-of 2026-10-05")]
    [InlineData("reports balance-sheet --as-of 2026-10-05 --basis cash")]
    [InlineData("reports profit-loss --from 2026-01-01 --to 2026-12-31")]
    public void ValidCliSelectionIsParsed(string arguments) => Assert.NotNull(Options.Parse(arguments.Split(' ')).Report);

    [Theory]
    [InlineData("reports trial-balance")]
    [InlineData("reports balance-sheet --as-of 2026-02-30")]
    [InlineData("reports balance-sheet --as-of 2026-10-05 --from 2026-01-01")]
    [InlineData("reports trial-balance --as-of 2026-10-05 --basis none")]
    [InlineData("reports trial-balance --as-of 2026-10-05 --as-of 2026-10-06")]
    [InlineData("reports profit-loss --from 2026-12-31 --to 2026-01-01")]
    [InlineData("reports profit-loss --from 2026-01-01")]
    [InlineData("reports profit-loss --from 2026-01-01 --to 2026-12-31 --as-of 2026-12-31")]
    [InlineData("reports trial-balance --as-of 2026-10-05 --page-size 1")]
    [InlineData("reports trial-balance --as-of 2026-10-05 --apply")]
    [InlineData("company info --basis cash")]
    public void InvalidCliSelectionsFailBeforeConnection(string arguments)
    {
        var output = new StringWriter();
        Assert.Equal(2, CliApplication.Run([..arguments.Split(' '), "--json"], (_, _) => throw new Exception("Must not connect"), output, new StringWriter()));
    }

    [Fact] public void CliUsesNormalizedReportAndClosesSession()
    {
        var processor = new Processor(); var output = new StringWriter();
        Assert.Equal(0, CliApplication.Run(["reports", "trial-balance", "--as-of", "2026-12-31", "--company-file", @"C:\Test.QBW", "--json"],
            (_, _) => new QuickBooksReader(processor, @"C:\Test.QBW"), output, new StringWriter()));
        Assert.Contains("\"kind\": \"subtotal\"", output.ToString());
        Assert.DoesNotContain("ReportRet", output.ToString());
        Assert.True(processor.Closed);
        Assert.Equal("false", processor.Request!.Descendants("DisplayReport").Single().Value);
    }

    [Fact] public void CompanyMismatchPreventsReportRequest()
    {
        var processor = new Processor();
        using var reader = new QuickBooksReader(processor, @"C:\Test.QBW");
        Assert.Equal("company_mismatch", Assert.Throws<QbException>(() => reader.ReadReport(Selection(), @"C:\Other.QBW")).Code);
        Assert.Null(processor.Request);
    }

    [Theory] [InlineData("1", "Info")] [InlineData("0", "Warn")] [InlineData("500", "Error")]
    public void NoMatchWarningAndErrorNeverReportComplete(string code, string severity)
    {
        var processor = new Processor { Code = code, Severity = severity };
        using var reader = new QuickBooksReader(processor, @"C:\Test.QBW");
        Assert.Throws<QbException>(() => reader.ReadReport(Selection(), ""));
    }

    private sealed class Processor : IRequestProcessor
    {
        public XDocument? Request;
        public string Code = "0", Severity = "Info";
        public bool Closed;
        public void Open() { }
        public void ConfigureAccess() { }
        public string Begin(string companyFile) => "ticket";
        public string CurrentCompanyFile(string ticket) => @"C:\Test.QBW";
        public string Process(string ticket, string request)
        {
            var xml = XDocument.Parse(request);
            if (xml.Descendants("HostQueryRq").Any()) return "<QBXML><QBXMLMsgsRs><HostQueryRs requestID='host' statusCode='0' statusSeverity='Info'><HostRet><ProductName>Test</ProductName><MajorVersion>34</MajorVersion><MinorVersion>0</MinorVersion><Country>US</Country><SupportedQBXMLVersion>13.0</SupportedQBXMLVersion></HostRet></HostQueryRs><CompanyQueryRs requestID='company' statusCode='0' statusSeverity='Info'><CompanyRet><CompanyName>Test</CompanyName></CompanyRet></CompanyQueryRs></QBXMLMsgsRs></QBXML>";
            Request = xml;
            return new XElement("QBXML", new XElement("QBXMLMsgsRs", new XElement("GeneralSummaryReportQueryRs",
                new XAttribute("requestID", "report"), new XAttribute("statusCode", Code), new XAttribute("statusSeverity", Severity), Report()))).ToString();
        }
        public void End(string ticket) { }
        public void Close() => Closed = true;
        public void Dispose() { }
    }
}
