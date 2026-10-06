using System.Text.Json.Nodes;

namespace QbCli;

/// <summary>Summarizes retained fixture outcomes without inferring current permissions or availability.</summary>
public static class SdkCoverageSummary
{
    public static JsonObject Catalog(IEnumerable<JsonNode> catalog)
    {
        var entries=catalog.ToArray();
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var entry in entries)
        {
            string name=entry["name"]!.GetValue<string>();
            if(string.IsNullOrWhiteSpace(name)||!names.Add(name))throw new InvalidOperationException("Coverage catalog operation names must be nonempty and unique.");
            if(entry["transportRole"]?.GetValue<string>() is not ("outbound_request" or "inbound_callback"))throw new InvalidOperationException("Unknown coverage transport role.");
        }
        var requests=entries.Where(e=>e["transportRole"]!.GetValue<string>()=="outbound_request").ToArray();
        var callbacks=entries.Where(e=>e["transportRole"]!.GetValue<string>()=="inbound_callback").ToArray();
        var states=requests.Select(e=>e["liveVerificationSummary"]!["state"]!.GetValue<string>()).ToArray();
        if(states.Any(s=>s is not ("success_recorded" or "no_match_only" or "attempted_without_acceptance" or "not_attempted")))throw new InvalidOperationException("Unknown live coverage state.");
        int Count(string state)=>states.Count(s=>s==state);
        int interfaces=requests.Count(e=>!string.IsNullOrWhiteSpace(e["sdkCommand"]?.GetValue<string>()));
        int succeeded=Count("success_recorded"),empty=Count("no_match_only"),accepted=succeeded+empty;
        decimal? Percent(int count)=>requests.Length==0?null:Math.Round(100m*count/requests.Length,1,MidpointRounding.AwayFromZero);
        return new JsonObject
        {
            ["documentedOutboundOperationCount"]=requests.Length,
            ["outboundCommandInterfaceCount"]=interfaces,
            ["commandInterfaceCoveragePercent"]=Percent(interfaces),
            ["acceptedLiveOperationCount"]=accepted,
            ["acceptedLiveCoveragePercent"]=Percent(accepted),
            ["successRecordedOperationCount"]=succeeded,
            ["successRecordedCoveragePercent"]=Percent(succeeded),
            ["noMatchOnlyOperationCount"]=empty,
            ["attemptedWithoutAcceptanceOperationCount"]=Count("attempted_without_acceptance"),
            ["notAttemptedOperationCount"]=Count("not_attempted"),
            ["inboundCallbackOperationCount"]=callbacks.Length,
            ["inboundCallbackHandlerInterfaceCount"]=callbacks.Count(e=>e["implementationStatus"]?.GetValue<string>()=="implemented"),
            ["overallSdkOptionsCompletionPercent"]=null,
            ["exhaustiveFieldAndLifecycleVerification"]=false,
            ["interpretation"]="Percentages count documented outbound operation names, including requests without an interface or retained attempt. Callbacks have separate handler counts and never inflate request coverage. Accepted live coverage includes succeeded and valid no_match outcomes; success_recorded means at least one SDK succeeded outcome, not necessarily nonempty data. Repeated fixtures count an operation once. These percentages do not measure every field/option, full latest SDK schema, lifecycle combination, current-company availability or permission. Overall SDK option completion remains unknown."
        };
    }

    public static JsonObject Operation(IEnumerable<JsonNode> records)
    {
        var rows=records.ToArray();
        var counts=rows.GroupBy(r=>r["outcome"]!.GetValue<string>(),StringComparer.Ordinal)
            .ToDictionary(g=>g.Key,g=>g.Count(),StringComparer.Ordinal);
        int Count(string key)=>counts.GetValueOrDefault(key);
        string state=Count("succeeded")>0?"success_recorded":Count("no_match")>0?"no_match_only":rows.Length>0?"attempted_without_acceptance":"not_attempted";
        return new JsonObject
        {
            ["state"]=state,
            ["fixtureOutcomeCount"]=rows.Length,
            ["outcomeCounts"]=new JsonObject(counts.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>new KeyValuePair<string,JsonNode?>(p.Key,JsonValue.Create(p.Value)))),
            ["statusCodes"]=new JsonArray(rows.Where(r=>r["statusCode"] is not null).Select(r=>r["statusCode"]!.GetValue<int>()).Distinct().Order().Select(n=>(JsonNode?)JsonValue.Create(n)).ToArray()),
            ["missingStatusCount"]=rows.Count(r=>r["statusCode"] is null),
            ["protocols"]=new JsonArray(rows.Select(r=>r["protocol"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(s=>(JsonNode?)JsonValue.Create(s)).ToArray()),
            ["availabilityInConnectedCompany"]=null,
            ["exhaustiveFieldAndLifecycleVerification"]=false
        };
    }
}
