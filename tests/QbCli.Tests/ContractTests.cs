using System.Text.Json;
using System.Xml.Linq;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class ContractTests
{
    private const string CompanyXml = "<QBXML><QBXMLMsgsRs><CompanyQueryRs statusCode='0' statusSeverity='Info'><CompanyRet><CompanyName>Test &amp; Co</CompanyName><LegalCompanyName>Test LLC</LegalCompanyName></CompanyRet></CompanyQueryRs></QBXMLMsgsRs></QBXML>";
    private const string AccountRet = "<AccountRet><ListID>1</ListID><Name>Checking</Name><FullName>Assets:Checking</FullName><AccountType>Bank</AccountType><IsActive>true</IsActive></AccountRet>";
    private static string AccountsXml(string body = "", string code = "0", string severity = "Info") => $"<QBXML><QBXMLMsgsRs><AccountQueryRs statusCode='{code}' statusSeverity='{severity}'>{body}</AccountQueryRs></QBXMLMsgsRs></QBXML>";

    [Fact] public void CompanyNormalizesEscapedText() => Assert.Equal(new CompanyInfo("Test & Co", "Test LLC"), QbXml.Company(CompanyXml));
    [Fact] public void AccountsNormalizes() => Assert.Equal(new Account("1", "Checking", "Assets:Checking", "Bank", true), Assert.Single(QbXml.Accounts(AccountsXml(AccountRet))));
    [Theory] [InlineData("false", false)] [InlineData("0", false)] [InlineData("1", true)]
    public void AccountsParsesBoolean(string value, bool active) => Assert.Equal(active, QbXml.Accounts(AccountsXml(AccountRet.Replace("true", value)))[0].Active);
    [Fact] public void EmptyAccountListIsSuccessful() => Assert.Empty(QbXml.Accounts(AccountsXml()));
    [Fact] public void NoMatchStatusIsEmpty() => Assert.Empty(QbXml.Accounts(AccountsXml(code: "1")));
    [Theory] [InlineData("ListID")] [InlineData("Name")] [InlineData("FullName")] [InlineData("AccountType")]
    public void MissingRequiredFieldsFail(string field)
    {
        var ret = XElement.Parse(AccountRet); ret.Element(field)!.Remove();
        Assert.Equal("invalid_response", Assert.Throws<QbException>(() => QbXml.Accounts(AccountsXml(ret.ToString()))).Code);
    }
    [Fact] public void MissingActiveIsUnknown() => Assert.Null(QbXml.Accounts(AccountsXml(AccountRet.Replace("<IsActive>true</IsActive>", "")))[0].Active);
    [Fact] public void MissingCompanyNameIsNull() => Assert.Null(QbXml.Company(CompanyXml.Replace("<CompanyName>Test &amp; Co</CompanyName>", "")).Name);
    [Fact] public void InvalidBooleanFails() => Assert.Throws<QbException>(() => QbXml.Accounts(AccountsXml(AccountRet.Replace("true", "maybe"))));
    [Theory] [InlineData("500", "Error")] [InlineData("1", "Warn")] [InlineData("0", "Warn")]
    public void SdkErrorsAndWarningsFail(string code, string severity)
    {
        var ex = Assert.Throws<QbException>(() => QbXml.Accounts(AccountsXml(AccountRet, code, severity)));
        Assert.Equal("quickbooks_status", ex.Code); Assert.Equal(code, ex.DiagnosticCode);
    }
    [Theory] [InlineData("<QBXML/>")] [InlineData("invalid")] [InlineData("<QBXML><QBXMLMsgsRs><AccountQueryRs/></QBXMLMsgsRs></QBXML>")]
    public void MalformedResponsesFail(string xml) => Assert.Throws<QbException>(() => QbXml.Accounts(xml));
    [Fact] public void ExternalEntitiesAreProhibited() => Assert.Throws<QbException>(() => QbXml.Accounts("<!DOCTYPE QBXML [<!ENTITY x SYSTEM 'file:///C:/secret'>]>" + AccountsXml("&x;")));
    [Fact] public void UnexpectedResponseFails() => Assert.Throws<QbException>(() => QbXml.Accounts(CompanyXml));
    [Fact] public void MultipleResponsesFail() => Assert.Throws<QbException>(() => QbXml.Accounts(AccountsXml().Replace("</QBXMLMsgsRs>", "<AccountQueryRs statusCode='0' statusSeverity='Info'/></QBXMLMsgsRs>")));
    [Fact] public void PartialResultsFail() => Assert.Equal("incomplete_response", Assert.Throws<QbException>(() => QbXml.Accounts(AccountsXml().Replace("statusCode=", "iteratorRemainingCount='1' statusCode="))).Code);
    [Fact] public void RequestsOnlyContainAllowedReads()
    {
        var company = XDocument.Parse(QbXml.CompanyRequest());
        Assert.Equal("CompanyQueryRq", Assert.Single(company.Root!.Element("QBXMLMsgsRq")!.Elements()).Name.LocalName);
        var accounts = XDocument.Parse(QbXml.AccountsRequest());
        var rq = Assert.Single(accounts.Root!.Element("QBXMLMsgsRq")!.Elements());
        Assert.Equal("AccountQueryRq", rq.Name.LocalName);
        Assert.Equal("All", rq.Element("ActiveStatus")!.Value);
        Assert.Null(rq.Element("MaxReturned")); // no silent truncation of the chart of accounts
    }
    [Fact] public void ApplicationSortsWithOrdinalTieBreaker()
    {
        using var reader = new StubReader();
        var result = new ReadService(reader).Accounts();
        Assert.Equal(new[] { "1", "2" }, result.Accounts.Select(a => a.Id));
    }
    [Fact] public void JsonIsCamelCaseAndHasNoVendorRepresentation()
    {
        var text = Output.Json(new ReadService(new StubReader()).Accounts());
        using var json = JsonDocument.Parse(text);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Bank", json.RootElement.GetProperty("accounts")[0].GetProperty("type").GetString());
        Assert.DoesNotContain("QBXML", text);
    }
    [Fact] public void TableNeutralizesControlCharacters() => Assert.Equal("a b c", Output.Safe("a\nb\u001bc"));
    [Fact] public void TableIncludesInactiveAccounts()
    {
        var writer = new StringWriter(); Output.Write(writer, new ReadService(new StubReader()).Accounts(), false);
        Assert.Contains("No", writer.ToString()); Assert.Contains("including inactive", writer.ToString());
    }
    [Theory] [InlineData("invoice", "create")] [InlineData("execute", "xml")] [InlineData("accounts", "--unknown")]
    public void UnsupportedCommandsFailBeforeConnection(string first, string second)
    {
        var stdout = new StringWriter();
        var exit = CliApplication.Run([first, second, "--json"], (_, _) => throw new Exception("Must not connect"), stdout, new StringWriter());
        Assert.Equal(2, exit);
        Assert.Contains("invalid_arguments", stdout.ToString());
    }
    [Fact] public void OptionsAcceptFlagsInAnyPosition() => Assert.Equal(new Options("accounts list", true, true, ""), Options.Parse(["--json", "accounts", "--verbose", "list"]));
    [Theory] [InlineData("--company-file")] [InlineData("--company-file", "relative.qbw")] [InlineData("--company-file", "--json")]
    public void BadFileArgumentsFail(params string[] args) => Assert.Throws<QbException>(() => Options.Parse(args));
    [Fact] public void JsonCommandWritesOnlyOneDocumentAndDisposesReader()
    {
        var reader = new StubReader(); var stdout = new StringWriter(); var stderr = new StringWriter();
        Assert.Equal(0, CliApplication.Run(["accounts", "list", "--json"], (_, _) => reader, stdout, stderr));
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(2, json.RootElement.GetProperty("accounts").GetArrayLength()); Assert.True(reader.Disposed); Assert.Equal("", stderr.ToString());
    }
    [Fact] public void ErrorDoesNotLeakVendorMessage()
    {
        var stdout = new StringWriter();
        Assert.Equal(4, CliApplication.Run(["company", "info", "--json"], (_, _) => throw new System.Runtime.InteropServices.COMException("secret data", -1), stdout, new StringWriter()));
        Assert.DoesNotContain("secret data", stdout.ToString()); Assert.Contains("0xFFFFFFFF", stdout.ToString());
    }
    [Fact] public void HelpAndDiagnosticsDoNotConnect()
    {
        foreach (var args in new[] { new[] { "--help", "--json" }, new[] { "diagnostics", "--json" } })
            Assert.Equal(0, CliApplication.Run(args, (_, _) => throw new Exception("Must not connect"), new StringWriter(), new StringWriter()));
    }
    [Fact] public void ExplicitReadJournalIsPassedWithoutGrantingWriteIntent()
    {
        var journal=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".jsonl");
        var reader=new JournalReader();
        var output=new StringWriter();
        Assert.Equal(0,CliApplication.Run(["sdk","run","ClassQuery","--apply","--company-file",@"C:\Test.QBW","--journal",journal],
            (options,_)=>{Assert.False(options.Apply);return reader;},output,new StringWriter()));
        Assert.Equal(journal,reader.Journal);
        Assert.True(reader.Disposed);
    }
    private sealed class JournalReader : IBooksReader,ISdkExecutor
    {
        public string? Journal;public bool Disposed;
        public SdkExecutionResult ExecuteSdk(SdkPlan plan,string company,string? journal)
        {
            Journal=journal;
            return new(true,1,company,"digest",false,false,journal,[]);
        }
        public SdkQueryResult QuerySdkAll(SdkPlan plan,string company,int pageSize)=>throw new NotImplementedException();
        public ConnectionInfo GetConnectionInfo()=>throw new NotImplementedException();
        public IReadOnlyList<Party> GetParties(PartyKind kind,PartySelection selection,int pageSize)=>[];
        public CompanyInfo GetCompany()=>new("Test",null);
        public IReadOnlyList<Account> GetAccounts()=>[];
        public void Dispose()=>Disposed=true;
    }

    private sealed class StubReader : IBooksReader
    {
        public bool Disposed;
        public ConnectionInfo GetConnectionInfo() => throw new NotImplementedException();
        public IReadOnlyList<Party> GetParties(PartyKind kind, PartySelection selection, int pageSize) => [];
        public CompanyInfo GetCompany() => new("Test", null);
        public IReadOnlyList<Account> GetAccounts() => [new("2", "A", "A", "Bank", false), new("1", "A", "A", "Bank", true)];
        public void Dispose() => Disposed = true;
    }
}

