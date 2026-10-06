using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class SdkSchemaTests
{
    [Theory]
    [InlineData("13.0","BOX14","US",true)]
    [InlineData("13.0","BOX15","US",false)]
    [InlineData("14.0","BOX14","US",false)]
    [InlineData("14.0","BOX15","US",true)]
    [InlineData("17.0","BOX15","US",true)]
    [InlineData("15.0","BOX16","US",false)]
    [InlineData("16.0","BOX16","US",true)]
    [InlineData("17.0","BOX16","US",true)]
    [InlineData("15.0","BOX14","US",false)]
    [InlineData("16.0","BOX14","US",true)]
    [InlineData("17.0","BOX14","US",false)]
    [InlineData("17.0","BOX16","AU",false)]
    [InlineData("17.0","BOX15","AU",false)]
    [InlineData("17.0","BOX14","AU",true)]
    public void MappingEnumsRespectDocumentedCountryAndVersionChanges(string version,string category,string country,bool allowed)
    {
        var query=new JsonObject{{"MappingCategory",category}};
        var modify=new JsonObject{{"Form1099CategoryAccountMappingMod",new JsonObject{
            {"CategoryAccountMappingMod",new JsonObject{{"MappingCategory",category}}}}}};
        foreach(var (operation,fields) in new[]{("Form1099CategoryAccountMappingQuery",query),("Form1099CategoryAccountMappingMod",modify)})
        {
            if(allowed)Assert.Equal(category,SdkXmlBuilder.Request(operation,fields,"enum",version,country).Descendants("MappingCategory").Single().Value);
            else Assert.Throws<QbException>(()=>SdkXmlBuilder.Request(operation,fields,"enum",version,country));
        }
    }
    public static IEnumerable<object[]> Operations() => SdkReference.Operations.Values.Select(o => new object[] { o.Name });
    [Theory] [MemberData(nameof(Operations))]
    public void EveryImportedRequestCanConstructItsRequiredDocumentedStructure(string name)
    {
        var operation = SdkReference.Get(name);
        if (!operation.DesktopAvailable)
        {
            Assert.Throws<QbException>(() => SdkXmlBuilder.Request(name, new(), "test"));
            return;
        }
        string country = new[] { "US", "CA", "UK", "AU" }.First(c => operation.Versions.TryGetValue(c, out var min) && decimal.Parse(min, CultureInfo.InvariantCulture) > 0);
        var fields = Sample(operation.Request, country);
        var xml = SdkXmlBuilder.Request(name, fields, "test", country: country);
        Assert.Equal(name + "Rq", xml.Name.LocalName);
        Assert.Equal("test", xml.Attribute("requestID")!.Value);
        Assert.DoesNotContain(xml.Descendants(), e => e.Name.LocalName is "OR" or "nothing");
    }
    [Fact] public void BuilderRejectsUnknownFieldsBeforeAnyConnection()
    {
        Assert.Throws<QbException>(() => SdkXmlBuilder.Request("CustomerAdd", JsonNode.Parse("{\"CustomerAdd\":{\"Name\":\"Test\",\"ExecuteXml\":\"x\"}}")!.AsObject(), "1"));
        Assert.Throws<QbException>(() => SdkXmlBuilder.Request("NotReal", new(), "1"));
    }
    [Fact] public void DefaultEnumAnnotationsAreNotExecutableEnumText()
    {
        var fields=JsonNode.Parse("{\"LeadAdd\":{\"FullName\":\"Fictional Lead\",\"Status\":\"Hot\"}}")!.AsObject();
        Assert.Equal("Hot",SdkXmlBuilder.Request("LeadAdd",fields,"1").Descendants("Status").Single().Value);
        fields["LeadAdd"]!["Status"]="Hot [Default]";
        Assert.Throws<QbException>(()=>SdkXmlBuilder.Request("LeadAdd",fields,"1"));
    }
    [Fact] public void PrivateEnumerationAnnotationsAreDiscoveryFactsNotPublicWireValues()
    {
        var schema=SdkReference.Get("TxnDel").Request.Children.Single(c=>c.Name=="TxnDelType");
        Assert.Contains("PayrollLiabilityAdjustment",schema.ExcludedPrivateEnumValues);
        foreach(var value in new[]{"PayrollLiabilityAdjustment","PayrollLiabilityAdjustment [PRIVATE]"})
        {
            var fields=new JsonObject{{"TxnDelType",value},{"TxnID","fictional"}};
            Assert.Throws<QbException>(()=>SdkXmlBuilder.Request("TxnDel",fields,"1"));
        }
    }
    [Fact] public void BuilderEnforcesRequiredFieldsTypesEnumsLengthsChoicesAndVersions()
    {
        foreach (var input in new[] { "{}", "{\"AccountAdd\":{\"Name\":\"X\"}}", "{\"AccountAdd\":{\"Name\":\"X\",\"AccountType\":\"NotAType\"}}", "{\"AccountAdd\":{\"Name\":\"X\",\"AccountType\":\"Bank\",\"IsActive\":1}}", "{\"AccountAdd\":{\"Name\":\"12345678901234567890123456789012\",\"AccountType\":\"Bank\"}}" })
            Assert.Throws<QbException>(() => SdkXmlBuilder.Request("AccountAdd", JsonNode.Parse(input)!.AsObject(), "1"));
        Assert.Throws<QbException>(() => SdkXmlBuilder.Request("CustomerQuery", JsonNode.Parse("{\"ListID\":\"x\",\"FullName\":\"x\"}")!.AsObject(), "1"));
        Assert.Throws<QbException>(() => SdkXmlBuilder.Request("CustomerQuery", JsonNode.Parse("{\"ClassFilter\":{\"ListID\":\"x\"}}")!.AsObject(), "1", "6.0"));
    }
    [Fact] public void BuilderUsesSchemaOrderAndEscapesText()
    {
        var input = JsonNode.Parse("{\"AccountAdd\":{\"AccountType\":\"Bank\",\"Name\":\"A&B <C>\"}}")!.AsObject();
        var request = SdkXmlBuilder.Request("AccountAdd", input, "1");
        Assert.Equal(new[] { "Name", "AccountType" }, request.Element("AccountAdd")!.Elements().Select(e => e.Name.LocalName));
        Assert.Contains("A&amp;B &lt;C&gt;", request.ToString());
    }
    [Fact] public void RepeatingChoiceAllowsDebitAndCreditArraysButSingleChoiceRemainsExclusive()
    {
        var fields = JsonNode.Parse("{\"JournalEntryAdd\":{\"JournalDebitLine\":[{\"AccountRef\":{\"FullName\":\"Expense\"},\"Amount\":\"1.00\"},{\"AccountRef\":{\"FullName\":\"Expense\"},\"Amount\":\"2.00\"}],\"JournalCreditLine\":{\"AccountRef\":{\"FullName\":\"Income\"},\"Amount\":\"3.00\"}}}")!.AsObject();
        var request = SdkXmlBuilder.Request("JournalEntryAdd", fields, "1");
        Assert.Equal(2, request.Descendants("JournalDebitLine").Count());
        Assert.Single(request.Descendants("JournalCreditLine"));
    }
    [Fact] public void RepeatedFieldsAndExactDecimalsSurvive()
    {
        var fields = JsonNode.Parse("{\"ListID\":[\"1\",\"2\"]}")!.AsObject();
        Assert.Equal(2, SdkXmlBuilder.Request("CustomerQuery", fields, "1").Elements("ListID").Count());
        var plan = new SdkPlan("13.0", "US", "stopOnError", [new("a", "AccountAdd", JsonNode.Parse("{\"AccountAdd\":{\"Name\":\"Test\",\"AccountType\":\"Bank\",\"OpenBalance\":\"1234.56\"}}")!.AsObject())]);
        Assert.Contains("1234.56", SdkBatch.Prepare(plan).Requests[0].ToString());
    }
    [Fact] public void OrderedChoicesPreserveInterleavedPostingOrder()
    {
        var fields = JsonNode.Parse("""
            {"JournalEntryAdd":{"$choices":[
              {"JournalDebitLine":{"AccountRef":{"FullName":"Expense"},"Amount":"1.00"}},
              {"JournalCreditLine":{"AccountRef":{"FullName":"Income"},"Amount":"3.00"}},
              {"JournalDebitLine":{"AccountRef":{"FullName":"Expense"},"Amount":"2.00"}}
            ]}}
            """)!.AsObject();
        var xml = SdkXmlBuilder.Request("JournalEntryAdd", fields, "1");
        Assert.Equal(new[] { "JournalDebitLine", "JournalCreditLine", "JournalDebitLine" }, xml.Element("JournalEntryAdd")!.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(new[] { "1.00", "3.00", "2.00" }, xml.Descendants("Amount").Select(e => e.Value));
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("[{}]")]
    [InlineData("[null]")]
    [InlineData("{}")]
    [InlineData("[{\"Unknown\":{}}]")]
    [InlineData("[{\"JournalDebitLine\":{\"AccountRef\":{\"FullName\":\"X\"},\"Amount\":\"1\"},\"Unknown\":1}]")]
    [InlineData("[{\"JournalDebitLine\":[],\"JournalCreditLine\":[]}]")]
    public void InvalidOrderedChoiceCannotBypassValidation(string choices)
    {
        var fields = JsonNode.Parse("{\"JournalEntryAdd\":{\"$choices\":" + choices + "}}")!.AsObject();
        Assert.Throws<QbException>(() => SdkXmlBuilder.Request("JournalEntryAdd", fields, "1"));
    }
    [Fact] public void OrderedChoicesCannotMixWithGroupedFieldsOrAppearAtOtherLocations()
    {
        Assert.Throws<QbException>(() => SdkXmlBuilder.Request("JournalEntryAdd", JsonNode.Parse("{\"JournalEntryAdd\":{\"$choices\":[],\"JournalDebitLine\":[]}}")!.AsObject(), "1"));
        Assert.Throws<QbException>(() => SdkXmlBuilder.Request("ClassQuery", JsonNode.Parse("{\"$choices\":[]}")!.AsObject(), "1"));
    }
    [Fact] public void SupplementedFieldsRequireDocumentedProtocolAndCountry()
    {
        var fields = JsonNode.Parse("{\"FormType\":\"NEC\"}")!.AsObject();
        Assert.Throws<QbException>(() => SdkXmlBuilder.Request("Form1099CategoryAccountMappingQuery", fields, "1", "13.0"));
        Assert.Equal("NEC", SdkXmlBuilder.Request("Form1099CategoryAccountMappingQuery", fields, "1", "14.0").Element("FormType")!.Value);
        Assert.Throws<QbException>(() => SdkXmlBuilder.Request("Form1099CategoryAccountMappingQuery", fields, "1", "14.0", "AU"));
        var expiration = JsonNode.Parse("{\"BuildAssemblyAdd\":{\"ItemInventoryAssemblyRef\":{\"FullName\":\"Assembly\"},\"QuantityToBuild\":\"1\",\"ExpirationDateForSerialLotNumber\":\"2027-01-01\"}}")!.AsObject();
        Assert.Throws<QbException>(() => SdkXmlBuilder.Request("BuildAssemblyAdd", expiration, "1", "13.0"));
        Assert.Equal("2027-01-01", SdkXmlBuilder.Request("BuildAssemblyAdd", expiration, "1", "16.0").Descendants("ExpirationDateForSerialLotNumber").Single().Value);
        Assert.Throws<QbException>(() => SdkXmlBuilder.Request("BuildAssemblyAdd", expiration, "1", "16.0", "UK"));
        Assert.Throws<QbException>(() => SdkXmlBuilder.Request("ClassQuery", new(), "1", "18.0"));
    }
    [Fact] public void SalesChannelSupplementIsIndependentOfOrderedLineChoices()
    {
        var fields = JsonNode.Parse("{\"SalesOrderAdd\":{\"CustomerRef\":{\"FullName\":\"Customer\"},\"$choices\":[{\"SalesOrderLineAdd\":{\"ItemRef\":{\"FullName\":\"Service\"},\"Quantity\":\"1\"}}],\"SOChannel\":\"Ecommerce\",\"StoreName\":\"Synthetic Store\",\"StoreType\":\"Shopify\"}}")!.AsObject();
        Assert.Throws<QbException>(() => SdkXmlBuilder.Request("SalesOrderAdd", fields, "1", "13.0"));
        var request = SdkXmlBuilder.Request("SalesOrderAdd", fields, "1", "16.0");
        var names = request.Element("SalesOrderAdd")!.Elements().Select(e => e.Name.LocalName).ToArray();
        Assert.True(Array.IndexOf(names,"SalesOrderLineAdd") < Array.IndexOf(names,"SOChannel"));
        Assert.Equal("Ecommerce",request.Descendants("SOChannel").Single().Value);
        fields["SalesOrderAdd"]!["SOChannel"]="UndocumentedValue";
        Assert.Throws<QbException>(() => SdkXmlBuilder.Request("SalesOrderAdd", fields, "1", "16.0"));
    }
    [Fact] public void ImportedEnumsContainNoEmptyOrPrivateMarkers()
    {
        void Check(SdkNode node)
        {
            Assert.All(node.EnumValues, value => { Assert.False(string.IsNullOrWhiteSpace(value)); Assert.DoesNotContain("[PRIVATE]",value); });
            foreach(var child in node.Children.Concat(node.Attributes)) Check(child);
        }
        foreach(var op in SdkReference.Operations.Values) { Check(op.Request); Check(op.Response); }
    }
    [Fact] public void MutationsPreviewWithoutConstructingReader()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "{\"ClassAdd\":{\"Name\":\"RentalA\"}}");
            var output = new StringWriter();
            Assert.Equal(0, CliApplication.Run(["sdk", "run", "ClassAdd", "--input", file], (_, _) => throw new Exception("Must not connect"), output, new StringWriter()));
            using var doc = JsonDocument.Parse(output.ToString());
            Assert.False(doc.RootElement.GetProperty("submitted").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("requiresApply").GetBoolean());
        }
        finally { File.Delete(file); }
    }
    [Fact] public void StrictInputRejectsDuplicateKeysAndApplyWithoutCompany()
    {
        var file = Path.GetTempFileName();
        try { File.WriteAllText(file, "{\"Name\":\"A\",\"Name\":\"B\"}"); Assert.Throws<QbException>(() => SdkInput.Read(file)); }
        finally { File.Delete(file); }
        Assert.Throws<QbException>(() => Options.Parse(["sdk", "run", "ClassAdd", "--apply"]));
    }
    private static bool Available(SdkNode n, string country) => (!n.Supports.TryGetValue(country, out var yes) || yes)
        && (!n.Versions.TryGetValue(country, out var min) || decimal.Parse(min, CultureInfo.InvariantCulture) is > 0 and <= 13);
    private static JsonObject Sample(SdkNode node, string country)
    {
        var result = new JsonObject();
        foreach (var child in node.Children.Where(c => c.Required)) Add(child, result, country);
        return result;
    }
    private static void Add(SdkNode node, JsonObject parent, string country)
    {
        if (node.Type == "skipwithchildren") return;
        if (!Available(node, country)) return;
        if (node.Type == "OR") { Add(node.Children.First(c => Available(c, country) && c.Type != "skipwithchildren"), parent, country); return; }
        if (node.Type == "skip") { foreach (var child in node.Children.Where(c => c.Required)) Add(child, parent, country); return; }
        if (node.Type == "Aggregate") { parent[node.Name] = Sample(node, country); return; }
        parent[node.Name] = node.Type switch
        {
            "BOOLTYPE" => JsonValue.Create(true),
            "INTTYPE" => JsonValue.Create(node.Minimum is { } min ? int.Parse(min, CultureInfo.InvariantCulture) : 1),
            "AMTTYPE" or "PRICETYPE" or "QUANTYPE" or "PERCENTTYPE" or "FLOATTYPE" => JsonValue.Create("1.00"),
            "DATETYPE" => JsonValue.Create("2026-10-05"),
            "DATETIMETYPE" => JsonValue.Create("2026-10-05T12:00:00"),
            "GUIDTYPE" or "UUIDTYPE" => JsonValue.Create("{11111111-1111-1111-1111-111111111111}"),
            "TIMEINTERVALTYPE" => JsonValue.Create("PT1H"),
            "ENUMTYPE" => JsonValue.Create(node.EnumValues.FirstOrDefault() ?? "NoMetaData"),
            _ => JsonValue.Create("X")
        };
    }
}
