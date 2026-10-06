using System.Text.Json.Nodes;
using System.Xml.Linq;
using QbCli;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class MacroTests
{
    private static SdkCall Define(string id, string name) => new(id, "InvoiceAdd",
        new JsonObject { ["InvoiceAdd"] = new JsonObject
        {
            ["$attributes"] = new JsonObject { ["defMacro"] = name },
            ["CustomerRef"] = new JsonObject { ["FullName"] = "Synthetic customer" },
            ["InvoiceLineAdd"] = new JsonObject { ["ItemRef"] = new JsonObject { ["FullName"] = "Synthetic item" }, ["Amount"] = "1.00" }
        }});
    private static SdkCall Use(string id, string name) => new(id, "ReceivePaymentAdd",
        new JsonObject { ["ReceivePaymentAdd"] = new JsonObject
        {
            ["CustomerRef"] = new JsonObject { ["FullName"] = "Synthetic customer" },
            ["TotalAmount"] = "1.00",
            ["AppliedToTxnAdd"] = new JsonObject { ["TxnID"] = new JsonObject
            { ["$value"] = "", ["$attributes"] = new JsonObject { ["useMacro"] = name } }, ["PaymentAmount"] = "1.00" }
        }});
    private static SdkPlan Plan(params SdkCall[] calls) => new("13.0", "US", "stopOnError", calls);

    [Theory]
    [InlineData("TxnID:1234", true)] [InlineData("TxnLineID:a-b_C", true)]
    [InlineData("TxnID:", false)] [InlineData("TxnID:a b", false)]
    [InlineData("TxnID:a.b", false)] [InlineData("TxnID:é", false)]
    [InlineData("TxnID:a;TxnLineID:b", false)] [InlineData("TxnID:a\n", false)]
    [InlineData("1234", false)] [InlineData("1TxnID:a", false)]
    public void MacroShapeIsCheckedByFieldBuilder(string name, bool valid)
    {
        var call = Define("i", name);
        if (valid) SdkXmlBuilder.Request(call.Operation, call.Fields, call.RequestId);
        else Assert.Throws<QbException>(() => SdkXmlBuilder.Request(call.Operation, call.Fields, call.RequestId));
    }

    [Fact] public void NameLimitAppliesAfterTagPrefix()
    {
        SdkBatch.Prepare(Plan(Define("i", "TxnID:" + new string('a', 40))));
        Assert.Throws<QbException>(() => SdkBatch.Prepare(Plan(Define("i", "TxnID:" + new string('a', 41)))));
    }

    [Fact] public void MacrosRemainValidAcrossCountAndByteBoundaries()
    {
        var plan = Plan(Define("i", "TxnID:invoice"), Use("p", "TxnID:invoice"));
        Assert.Single(SdkBatch.Messages(SdkBatch.Prepare(plan)));
        Assert.Equal(2, SdkBatch.Messages(SdkBatch.Prepare(plan with { BatchSize = 1 })).Count);
        // Padding affects serialized bytes without introducing another macro definition.
        plan.Requests[0].Fields["InvoiceAdd"]!["Memo"] = new string('x', 600);
        Assert.Equal(2, SdkBatch.Messages(SdkBatch.Prepare(plan with { MaximumBatchBytes = 1024 })).Count);
    }

    [Fact] public void UndefinedForwardAndDuplicateDefinitionsFailBeforeExecution()
    {
        Assert.Throws<QbException>(() => SdkBatch.Prepare(Plan(Use("p", "TxnID:missing"))));
        Assert.Throws<QbException>(() => SdkBatch.Prepare(Plan(Use("p", "TxnID:invoice"), Define("i", "TxnID:invoice"))));
        Assert.Throws<QbException>(() => SdkBatch.Prepare(Plan(Define("i", "TxnID:invoice"), Define("i2", "TxnID:invoice"))));
        Assert.Throws<QbException>(() => SdkBatch.Prepare(Plan(Define("i", "TxnID:invoice"), Use("p", "TxnID:Invoice"))));
    }

    [Fact] public void SameRequestDefinitionDoesNotSatisfyReference()
    {
        var call = Use("p", "TxnID:payment");
        call.Fields["ReceivePaymentAdd"]!["$attributes"] = new JsonObject { ["defMacro"] = "TxnID:payment" };
        Assert.Throws<QbException>(() => SdkBatch.Prepare(Plan(call)));
    }

    [Fact] public void MultipleLaterReferencesMayUseTheSameDefinition()
    {
        var prepared = SdkBatch.Prepare(Plan(Define("i", "TxnID:invoice"), Use("p1", "TxnID:invoice"), Use("p2", "TxnID:invoice")));
        Assert.Equal(3, prepared.Requests.Count);
    }

    [Fact] public void DuplicateLineAndTransactionDefinitionsAreRejected()
    {
        var call = Define("i", "TxnID:invoice");
        call.Fields["InvoiceAdd"]!["InvoiceLineAdd"]!["$attributes"] = new JsonObject { ["defMacro"] = "TxnID:invoice" };
        Assert.Throws<QbException>(() => SdkBatch.Prepare(Plan(call)));
    }

    private static string[] InterruptedJournal(bool invoiceCompleted)
    {
        var blocker = new SdkCall("b", "ClassAdd", new JsonObject { ["ClassAdd"] = new JsonObject { ["Name"] = "Synthetic duplicate" }});
        var invoice = Define("i", "TxnID:invoice");
        var payment = Use("p", "TxnID:invoice");
        var plan = Plan(invoiceCompleted ? [invoice, blocker, payment] : [blocker, invoice, payment]) with { BatchSize = 1 };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            SdkBatch.Execute(SdkBatch.Prepare(plan), @"C:\test\Test.QBW", path, xml =>
            {
                var request = XDocument.Parse(xml).Root!.Element("QBXMLMsgsRq")!.Elements().Single();
                var id = request.Attribute("requestID")!.Value;
                var code = id == "b" ? 3100 : 0;
                return new XElement("QBXML", new XElement("QBXMLMsgsRs", new XElement(
                    request.Name.LocalName[..^2] + "Rs", new XAttribute("requestID", id),
                    new XAttribute("statusCode", code), new XAttribute("statusSeverity", code == 0 ? "Info" : "Error")))).ToString();
            });
            var lines=File.ReadAllLines(path);
            var header=JsonNode.Parse(lines[0])!.AsObject(); header.Remove("sourceProcess");
            lines[0]=header.ToJsonString(); // Legacy process-independent macro fixture.
            return lines;
        }
        finally { File.Delete(path); }
    }

    [Fact] public void ResumePermitsOnlyAWholeProvenUnexecutedMacroChain()
    {
        var resume = SdkResume.Build(InterruptedJournal(false), 1);
        Assert.True(resume.CanApply);
        Assert.Equal(new[] { "i", "p" }, resume.PendingPlan.Requests.Select(c => c.RequestId));
        Assert.Equal(2, SdkBatch.Messages(SdkBatch.Prepare(resume.PendingPlan)).Count);
        var error = Assert.Throws<QbException>(() => SdkResume.Build(InterruptedJournal(true)));
        Assert.Contains("not self-contained", error.Message);
    }

    [Fact] public void DiscoveryExplainsSessionLifetimeAndResumeDependencies()
    {
        var contract = CommandCatalog.Discover()["macroContract"]!;
        Assert.Contains("EndSession", contract["session"]!.GetValue<string>());
        Assert.Contains("self-contained pending macro chains", contract["failure"]!.GetValue<string>());
    }
}
