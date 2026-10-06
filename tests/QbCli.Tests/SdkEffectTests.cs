using System.Text.Json.Nodes;
using QbCli;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class SdkEffectTests
{
    [Theory]
    [InlineData("ClassQuery", false, false, false, false, false)]
    [InlineData("ClassAdd", true, true, false, false, false)]
    [InlineData("DataEventRecoveryInfoQuery", false, false, false, false, false)]
    [InlineData("DataEventRecoveryInfoDel", true, false, true, false, false)]
    [InlineData("ListDisplayAdd", true, false, false, false, true)]
    [InlineData("ListDisplayMod", true, false, false, false, true)]
    [InlineData("TxnDisplayAdd", true, false, false, false, true)]
    [InlineData("TxnDisplayMod", true, false, false, false, true)]
    [InlineData("DataEventSubscriptionQuery", false, false, false, false, false)]
    [InlineData("SubscriptionDel", true, false, false, true, false)]
    public void ApplyIntentIsSeparateFromEffectCategory(string name, bool apply, bool company, bool recovery, bool subscription, bool ui)
    {
        var operation = SdkReference.Get(name);
        Assert.Equal(apply, operation.RequiresApply);
        Assert.Equal(company, operation.MutatesCompany);
        Assert.Equal(recovery, operation.MutatesRecoveryState);
        Assert.Equal(subscription, operation.MutatesSubscriptions);
        Assert.Equal(ui, operation.AffectsUserInterface);
    }

    private static SdkPlan RecoveryDelete() => new("13.0", "US", "stopOnError", [
        new("del", "DataEventRecoveryInfoDel", new() { ["SubscriberID"] = "{11111111-1111-1111-1111-111111111111}" })]);

    [Fact] public void FailedRecoveryDeleteRemainsAFailureAndReportsIntendedEffect()
    {
        var prepared = SdkBatch.Prepare(RecoveryDelete());
        Assert.True(prepared.RequiresApply);
        var result = SdkBatch.Execute(prepared, "Test.QBW", null, _ =>
            "<QBXML><QBXMLMsgsRs><DataEventRecoveryInfoDelRs requestID='del' statusCode='3120' statusSeverity='Error'/></QBXMLMsgsRs></QBXML>");
        Assert.False(result.Success);
        Assert.False(result.MutatesCompany);
        Assert.True(result.MutatesRecoveryState);
        Assert.False(result.MutatesSubscriptions || result.AffectsUserInterface);
        Assert.Equal("failed", result.Results.Single().Outcome);
    }

    [Fact] public void RecoveryDeletePreviewNeverConnectsAndStillRequiresExplicitApply()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, RecoveryDelete().Requests[0].Fields.ToJsonString());
            var output = new StringWriter();
            Assert.Equal(0, CliApplication.Run(["sdk", "run", "DataEventRecoveryInfoDel", "--input", path],
                (_, _) => throw new Exception("Preview must not connect"), output, new StringWriter()));
            var preview = JsonNode.Parse(output.ToString())!;
            Assert.True(preview["requiresApply"]!.GetValue<bool>());
            Assert.True(preview["mutatesRecoveryState"]!.GetValue<bool>());
            Assert.False(preview["mutatesCompany"]!.GetValue<bool>());
            Assert.False(preview["submitted"]!.GetValue<bool>());
        }
        finally { File.Delete(path); }
    }

    [Fact] public void MixedUiAndAccountingPlanReportsBothCategories()
    {
        var plan = new SdkPlan("13.0", "US", "stopOnError", [
            new("ui", "ListDisplayAdd", new() { ["ListDisplayAddType"] = "Account" }),
            new("add", "ClassAdd", new() { ["ClassAdd"] = new JsonObject { ["Name"] = "Synthetic class" } })]);
        var prepared = SdkBatch.Prepare(plan);
        Assert.True(prepared.RequiresApply && prepared.AffectsUserInterface && prepared.MutatesCompany);
        Assert.False(prepared.MutatesRecoveryState || prepared.MutatesSubscriptions);
    }

    [Fact] public void FullDiscoveryAndExpandedDefinitionsAgreeOnRecoveryEffects()
    {
        var operations = SdkReference.Discover()["operations"]!.AsArray();
        var delete = operations.Single(n => n!["name"]!.GetValue<string>() == "DataEventRecoveryInfoDel")!;
        Assert.True(delete["requiresApply"]!.GetValue<bool>() && delete["mutatesRecoveryState"]!.GetValue<bool>());
        Assert.False(delete["mutatesCompany"]!.GetValue<bool>());
    }
}
