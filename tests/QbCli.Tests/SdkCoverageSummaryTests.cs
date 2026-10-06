using System.Text.Json.Nodes;
using Xunit;

namespace QbCli.Tests;

public class SdkCoverageSummaryTests
{
    private static JsonNode Entry(string name,string role,string? command,params JsonNode[] evidence)=>new JsonObject
    {
        ["name"]=name,["transportRole"]=role,["sdkCommand"]=command,
        ["implementationStatus"]=role=="inbound_callback"?"implemented":command is null?"not_implemented":"schema_validated_interface",
        ["liveVerificationSummary"]=SdkCoverageSummary.Operation(evidence)
    };
    [Fact] public void MissingInterfacesStayInDenominatorAndCallbacksDoNotInflateCoverage()
    {
        var summary=SdkCoverageSummary.Catalog([
            Entry("Add","outbound_request","sdk run Add",Row("succeeded")),
            Entry("EmptyQuery","outbound_request","sdk run EmptyQuery",Row("no_match",1)),
            Entry("FeatureQuery","outbound_request","sdk run FeatureQuery",Row("warning",500)),
            Entry("Missing","outbound_request",null),
            Entry("Callback","inbound_callback",null,Row("succeeded"))]);
        Assert.Equal(4,summary["documentedOutboundOperationCount"]!.GetValue<int>());
        Assert.Equal(3,summary["outboundCommandInterfaceCount"]!.GetValue<int>());
        Assert.Equal(75m,summary["commandInterfaceCoveragePercent"]!.GetValue<decimal>());
        Assert.Equal(50m,summary["acceptedLiveCoveragePercent"]!.GetValue<decimal>());
        Assert.Equal(25m,summary["successRecordedCoveragePercent"]!.GetValue<decimal>());
        Assert.Equal(1,summary["inboundCallbackHandlerInterfaceCount"]!.GetValue<int>());
        Assert.Equal(1,summary["attemptedWithoutAcceptanceOperationCount"]!.GetValue<int>());
        Assert.Equal(1,summary["notAttemptedOperationCount"]!.GetValue<int>());
        Assert.Null(summary["overallSdkOptionsCompletionPercent"]);
        Assert.False(summary["exhaustiveFieldAndLifecycleVerification"]!.GetValue<bool>());
    }
    [Fact] public void RepeatedResultsDoNotBecomeMultipleCoveredOperations()
    {
        var summary=SdkCoverageSummary.Catalog([Entry("One","outbound_request","sdk run One",Row("failed",3250),Row("succeeded"),Row("succeeded"),Row("no_match",1))]);
        Assert.Equal(1,summary["acceptedLiveOperationCount"]!.GetValue<int>());
        Assert.Equal(1,summary["successRecordedOperationCount"]!.GetValue<int>());
        Assert.Equal(0,summary["noMatchOnlyOperationCount"]!.GetValue<int>());
        Assert.Equal(100m,summary["acceptedLiveCoveragePercent"]!.GetValue<decimal>());
    }
    [Fact] public void ZeroRequestDenominatorRemainsUnknown()
    {
        foreach(var entries in new[]{Array.Empty<JsonNode>(),new[]{Entry("Callback","inbound_callback",null)}})
        {
            var summary=SdkCoverageSummary.Catalog(entries);
            Assert.Null(summary["commandInterfaceCoveragePercent"]);
            Assert.Null(summary["acceptedLiveCoveragePercent"]);
            Assert.Null(summary["successRecordedCoveragePercent"]);
        }
    }
    [Fact] public void PercentagesRoundAgainstAllDocumentedRequests()
    {
        var summary=SdkCoverageSummary.Catalog([Entry("One","outbound_request","sdk run One",Row("succeeded")),Entry("Two","outbound_request","sdk run Two"),Entry("Three","outbound_request",null)]);
        Assert.Equal(33.3m,summary["acceptedLiveCoveragePercent"]!.GetValue<decimal>());
        Assert.Equal(66.7m,summary["commandInterfaceCoveragePercent"]!.GetValue<decimal>());
    }
    [Fact] public void DuplicateNamesAndUnknownTransportRolesAreRejected()
    {
        Assert.Throws<InvalidOperationException>(()=>SdkCoverageSummary.Catalog([Entry("One","outbound_request",null),Entry("One","outbound_request",null)]));
        Assert.Throws<InvalidOperationException>(()=>SdkCoverageSummary.Catalog([Entry("One","unrecognized",null)]));
    }
    [Fact] public void DiscoverySeparatesRequestInterfacesFromCallbackHandling()
    {
        var dump=CommandCatalog.Discover();var operations=dump["sdkCatalog"]!["operations"]!.AsArray();
        var callback=Assert.Single(operations,o=>o!["transportRole"]!.GetValue<string>()=="inbound_callback");
        Assert.Equal("QBXMLEvents",callback!["name"]!.GetValue<string>());
        Assert.Null(callback["sdkCommand"]);
        var coverage=dump["liveCoverageSummary"]!["requestOperationCoverage"]!;
        Assert.Equal(operations.Count-1,coverage["documentedOutboundOperationCount"]!.GetValue<int>());
        Assert.Equal(1,coverage["inboundCallbackOperationCount"]!.GetValue<int>());
        Assert.Null(coverage["overallSdkOptionsCompletionPercent"]);
    }
    private static JsonNode Row(string outcome,int? status=0,string protocol="13.0")=>new JsonObject
        {["outcome"]=outcome,["statusCode"]=status,["protocol"]=protocol};
    [Fact] public void EmptyEvidenceIsUnattemptedAndNeverClaimsAvailability()
    {
        var summary=SdkCoverageSummary.Operation([]);
        Assert.Equal("not_attempted",summary["state"]!.GetValue<string>());
        Assert.Equal(0,summary["fixtureOutcomeCount"]!.GetValue<int>());
        Assert.Null(summary["availabilityInConnectedCompany"]);
        Assert.False(summary["exhaustiveFieldAndLifecycleVerification"]!.GetValue<bool>());
    }
    [Fact] public void EmptyAcceptedQueriesRemainDistinctFromSucceededOutcomes()
    {
        var summary=SdkCoverageSummary.Operation([Row("no_match",1),Row("failed",3250)]);
        Assert.Equal("no_match_only",summary["state"]!.GetValue<string>());
        Assert.Equal(1,summary["outcomeCounts"]!["failed"]!.GetValue<int>());
    }
    [Fact] public void UnknownWarningAndUnprocessedNeverBecomeAccepted()
    {
        var summary=SdkCoverageSummary.Operation([Row("unknown",null),Row("warning",1000),Row("unprocessed",3231)]);
        Assert.Equal("attempted_without_acceptance",summary["state"]!.GetValue<string>());
        Assert.Equal(1,summary["missingStatusCount"]!.GetValue<int>());
        Assert.Equal(new[]{1000,3231},summary["statusCodes"]!.AsArray().Select(n=>n!.GetValue<int>()));
    }
    [Fact] public void LaterSuccessRetainsNegativeAndUnknownEvidenceWithSortedProtocols()
    {
        var summary=SdkCoverageSummary.Operation([Row("failed",3175,"16.0"),Row("succeeded"),Row("unknown",null),Row("succeeded")]);
        Assert.Equal("success_recorded",summary["state"]!.GetValue<string>());
        Assert.Equal(4,summary["fixtureOutcomeCount"]!.GetValue<int>());
        Assert.Equal(2,summary["outcomeCounts"]!["succeeded"]!.GetValue<int>());
        Assert.Equal(1,summary["outcomeCounts"]!["unknown"]!.GetValue<int>());
        Assert.Equal(new[]{"13.0","16.0"},summary["protocols"]!.AsArray().Select(n=>n!.GetValue<string>()));
    }
    [Fact] public void FullDiscoveryStatesAreDerivedFromEveryCatalogOperationsActualEvidence()
    {
        var dump=CommandCatalog.Discover();var ops=dump["sdkCatalog"]!["operations"]!.AsArray();
        foreach(var op in ops)
        {
            var expected=SdkCoverageSummary.Operation(op!["liveVerification"]!.AsArray().OfType<JsonNode>());
            Assert.True(JsonNode.DeepEquals(expected,op["liveVerificationSummary"]));
            Assert.Null(op["availableInConnectedCompany"]);
        }
        Assert.Equal(ops.Count,dump["liveCoverageSummary"]!["operationStateCounts"]!.AsObject().Sum(p=>p.Value!.GetValue<int>()));
    }
}
