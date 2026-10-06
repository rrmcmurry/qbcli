using System.Text.Json;
using System.Xml.Linq;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class DiscoveryTests
{
    [Theory] [InlineData("Check")] [InlineData("Bill")] [InlineData("CreditCardCharge")]
    [InlineData("CreditCardCredit")] [InlineData("VendorCredit")] [InlineData("ItemReceipt")]
    public void DiscoveredPurchaseItemBodyBuildsOrderedModifiersForEveryVerifiedType(string type)
    {
        var contract=CommandCatalog.Discover()["purchaseLineClassContract"]!;
        var fields=new System.Text.Json.Nodes.JsonObject {[type+"Mod"]=contract["exampleBody"]!.DeepClone()};
        var body=SdkXmlBuilder.Request(type+"Mod",fields,"example","13.0","US").Element(type+"Mod")!;
        Assert.Equal(new[]{"ItemLineMod","ItemGroupLineMod"},body.Elements().Where(n=>n.Name.LocalName.Contains("Line")).Select(n=>n.Name.LocalName));
        Assert.Single(body.Element("ItemGroupLineMod")!.Elements("ItemLineMod"));
        Assert.Equal("actual-flat-account-id",body.Element("ItemLineMod")!.Element("OverrideItemAccountRef")!.Element("ListID")!.Value);
        Assert.Null(body.Element("ExpenseLineMod"));
        Assert.Contains("Item and expense classes merge into one modifier per transaction",contract["status"]!.GetValue<string>());
    }
    [Fact] public void DiscoveredSalesClassExampleProducesOrderedTypedModifiers()
    {
        var contract=CommandCatalog.Discover()["salesLineClassContract"]!;
        var fields=contract["exampleFields"]!.DeepClone().AsObject();
        var xml=SdkXmlBuilder.Request("InvoiceMod",fields,"example","13.0","US");
        var body=xml.Element("InvoiceMod")!;
        Assert.Equal(new[]{"InvoiceLineMod","InvoiceLineGroupMod"},body.Elements().Where(n=>n.Name.LocalName.Contains("Line")).Select(n=>n.Name.LocalName));
        Assert.Equal(2,body.Element("InvoiceLineGroupMod")!.Elements("InvoiceLineMod").Count());
        Assert.Contains("TransactionQuery posting rows do not return ClassRef",contract["verification"]!.GetValue<string>());
        Assert.Contains("payment items",contract["status"]!.GetValue<string>());
        Assert.Contains("inventory revenue-account lines",contract["status"]!.GetValue<string>());
        Assert.Contains("Confirmed ItemSubtotalQuery identities are retained without an account override or new class",contract["planning"]!.GetValue<string>());
        Assert.Contains("COGS/asset account class selection",contract["status"]!.GetValue<string>());
    }

    [Fact] public void BehavioralEvidenceAppearsInBothFullDiscoveryAndExpandedSchema()
    {
        var dump=SdkReference.Discover();
        var deposit=dump["operations"]!.AsArray().Single(o=>o!["name"]!.GetValue<string>()=="DepositMod")!;
        Assert.Equal("deposit-existing-payment-link",deposit["behaviorNotes"]![0]!["id"]!.GetValue<string>());
        var output=new StringWriter();
        Assert.Equal(0,CliApplication.Run(["sdk","schema","DepositMod"],(_,_)=>throw new Exception("Must not connect"),output,new StringWriter()));
        using var parsed=JsonDocument.Parse(output.ToString());
        var note=parsed.RootElement.GetProperty("operation").GetProperty("behaviorNotes")[0];
        Assert.Contains("SDK3210",note.GetProperty("summary").GetString());
        Assert.Contains("synthetic live tests",note.GetProperty("evidenceScope").GetString());
        Assert.Contains(SdkReference.Get("CustomerQuery").BehaviorNotes,n=>n.Id=="private-extension-definition-deletion");
        Assert.Empty(SdkReference.Get("EmployeeQuery").BehaviorNotes);
    }
    [Fact] public void InternedDiscoveryReconstructsEveryExpandedSchema()
    {
        var dump = SdkReference.Discover();
        var nodes = dump["schemaNodes"]!.AsObject();
        System.Text.Json.Nodes.JsonNode Expand(string id)
        {
            var node = nodes[id]!.DeepClone().AsObject();
            foreach (var key in new[] { "children", "attributes" })
                if (node[key] is System.Text.Json.Nodes.JsonArray refs)
                    node[key] = new System.Text.Json.Nodes.JsonArray(refs.Select(r => Expand(r!.GetValue<string>())).ToArray());
            return node;
        }
        foreach (var op in dump["operations"]!.AsArray())
        {
            var definition = SdkReference.Get(op!["name"]!.GetValue<string>());
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            foreach (var (key, expected) in new[] { ("requestRef", definition.Request), ("responseRef", definition.Response) })
            {
                var expanded = Expand(op[key]!.GetValue<string>()).Deserialize<SdkNode>(options)!;
                Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(expanded));
            }
        }
    }
    [Fact] public void OfflineDumpNeverConnectsAndDistinguishesDocumentedOperations()
    {
        var output = new StringWriter();
        Assert.Equal(0, CliApplication.Run(["capabilities"], (_, _) => throw new Exception("Must not connect"), output, new StringWriter()));
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal("not_connected", root.GetProperty("connection").GetProperty("state").GetString());
        Assert.Equal(239, root.GetProperty("sdkCatalog").GetProperty("operationCount").GetInt32());
        Assert.Equal(1249,root.GetProperty("currentEnumAudit").GetProperty("comparedFields").GetInt32());
        Assert.Equal(0,root.GetProperty("currentEnumAudit").GetProperty("unparsed").GetArrayLength());
        Assert.Equal(24468,root.GetProperty("currentConstraintAudit").GetProperty("comparedFacts").GetProperty("types").GetInt32());
        Assert.Equal(0,root.GetProperty("currentConstraintAudit").GetProperty("differences").GetArrayLength());
        Assert.True(root.GetProperty("bulkChanges").GetProperty("implemented").GetBoolean());
        Assert.Equal(ReclassificationPlanner.SupportedTypes,root.GetProperty("bulkChanges").GetProperty("supportedTransactionTypes").EnumerateArray().Select(t=>t.GetString()));
        Assert.Contains(root.GetProperty("sdkCatalog").GetProperty("operations").EnumerateArray(), o => o.GetProperty("name").GetString() == "InvoiceMod" && o.GetProperty("implementationStatus").GetString() == "schema_validated_interface");
    }
    [Theory] [InlineData("customers")] [InlineData("vendors")]
    public void DirectorySelectorsAndOptionScopeAreValidated(string entity)
    {
        Assert.Equal(PartySelectionKind.Contains, Options.Parse([entity, "find", "A & B", "--page-size", "1"]).Selection.Kind);
        Assert.Equal(PartySelectionKind.FullName, Options.Parse([entity, "get", "--name", "Parent:Job"]).Selection.Kind);
        foreach (var args in new[] { new[] { entity, "get" }, new[] { entity, "get", "--id", "a", "--name", "b" }, new[] { entity, "list", "--id", "a" }, new[] { entity, "find" }, new[] { entity, "list", "--page-size", "0" } })
            Assert.Equal("invalid_arguments", Assert.Throws<QbException>(() => Options.Parse(args)).Code);
    }
    [Fact] public void ExactLookupEscapesXmlAndDoesNotUseIterator()
    {
        var xml = XDocument.Parse(QbXml.PartiesRequest(PartyKind.Customer, new(PartySelectionKind.FullName, "A&B:Job <1>"), 500));
        var rq = xml.Descendants("CustomerQueryRq").Single();
        Assert.Null(rq.Attribute("iterator"));
        Assert.Equal("A&B:Job <1>", rq.Element("FullName")!.Value);
    }
    [Fact] public void BatchedDiscoveryCorrelatesResponsesRatherThanPosition()
    {
        var host = "<HostQueryRs requestID='host' statusCode='0' statusSeverity='Info'><HostRet><ProductName>Test host</ProductName><MajorVersion>34</MajorVersion><MinorVersion>0</MinorVersion><SupportedQBXMLVersion>6.0</SupportedQBXMLVersion></HostRet></HostQueryRs>";
        var company = "<CompanyQueryRs requestID='company' statusCode='0' statusSeverity='Info'><CompanyRet><CompanyName>Test</CompanyName></CompanyRet></CompanyQueryRs>";
        Assert.Equal("Test", QbXml.Connection(Envelope(company + host)).Company.Name);
        Assert.Throws<QbException>(() => QbXml.Connection(Envelope(host + host)));
        Assert.Throws<QbException>(() => QbXml.Connection(Envelope(company)));
    }
    [Fact] public void IteratorReadsEveryPageThenFiltersWithoutLosingJobs()
    {
        var processor = new Pages(Page("1", "Parent:RentalA", 1), Page("2", "Other", 0));
        using var reader = new QuickBooksReader(processor, "");
        var records = reader.GetParties(PartyKind.Customer, new(PartySelectionKind.Contains, "rentala"), 1);
        Assert.Single(records); Assert.Equal("1", records[0].Id);
        Assert.Equal(2, processor.Requests.Count);
        Assert.Contains("iterator=\"Continue\"", processor.Requests[1]);
        Assert.Contains("iteratorID=\"it\"", processor.Requests[1]);
        Assert.Contains("IncludeRetElement", processor.Requests[1]);
    }
    [Theory] [InlineData("duplicate")] [InlineData("no_progress")] [InlineData("changed_iterator")]
    public void IncompletePaginationFailsInsteadOfReportingPartialSuccess(string failure)
    {
        string second = failure switch { "duplicate" => Page("1", "A", 0), "no_progress" => Page("2", "B", 2), _ => Page("2", "B", 1).Replace("iteratorID='it'", "iteratorID='other'") };
        var processor = new Pages(Page("1", "A", 2), second);
        Assert.Throws<QbException>(() => { using var reader = new QuickBooksReader(processor, ""); reader.GetParties(PartyKind.Customer, new(), 1); });
        Assert.True(processor.Ended);
    }
    [Theory] [InlineData("<CustomerQueryRs statusCode='0' statusSeverity='Info'/>")]
    [InlineData("<CustomerQueryRs statusCode='0' statusSeverity='Info' iteratorRemainingCount='1'/>")]
    [InlineData("<CustomerQueryRs statusCode='500' statusSeverity='Warn' iteratorRemainingCount='0'/>")]
    public void MalformedOrWarningPagesFail(string response) => Assert.Throws<QbException>(() => QbXml.Parties(Envelope(response), PartyKind.Customer, true));
    [Fact] public void EmptyNoMatchIsComplete() => Assert.Empty(QbXml.Parties(Envelope("<VendorQueryRs statusCode='1' statusSeverity='Info'/>"), PartyKind.Vendor, true).Records);
    [Fact] public void MissingExplicitObjectHasNotFoundErrorButBroadWarningStillFails()
    {
        var response = Envelope("<CustomerQueryRs statusCode='500' statusSeverity='Warn'/>");
        Assert.Equal("not_found", Assert.Throws<QbException>(() => QbXml.Parties(response, PartyKind.Customer, false)).Code);
        Assert.Equal("quickbooks_status", Assert.Throws<QbException>(() => QbXml.Parties(response, PartyKind.Customer, true)).Code);
    }
    private static string Envelope(string body) => "<QBXML><QBXMLMsgsRs>" + body + "</QBXMLMsgsRs></QBXML>";
    private static string Page(string id, string name, int remaining) => Envelope($"<CustomerQueryRs statusCode='0' statusSeverity='Info' iteratorID='it' iteratorRemainingCount='{remaining}'><CustomerRet><ListID>{id}</ListID><Name>{name}</Name><FullName>{name}</FullName></CustomerRet></CustomerQueryRs>");
    private sealed class Pages(params string[] responses) : IRequestProcessor
    {
        private readonly Queue<string> responses = new(responses);
        public List<string> Requests { get; } = [];
        public bool Ended;
        public void Open() { }
        public void ConfigureAccess() { }
        public string Begin(string companyFile) => "ticket";
        public string CurrentCompanyFile(string ticket) => "test.QBW";
        public string Process(string ticket, string request) { Requests.Add(request); return responses.Dequeue(); }
        public void End(string ticket) => Ended = true;
        public void Close() { }
        public void Dispose() { }
    }
}
