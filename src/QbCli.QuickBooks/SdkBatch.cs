using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using QbCli.Core;

namespace QbCli.QuickBooks;

public sealed record PreparedSdkPlan(SdkPlan Plan, IReadOnlyList<XElement> Requests, string Digest, bool RequiresApply)
{
    public bool MutatesCompany => Plan.Requests.Any(r => SdkReference.Get(r.Operation).MutatesCompany);
    public bool MutatesSubscriptions => Plan.Requests.Any(r => SdkReference.Get(r.Operation).MutatesSubscriptions);
    public bool AffectsUserInterface => Plan.Requests.Any(r => SdkReference.Get(r.Operation).AffectsUserInterface);
    public bool MutatesRecoveryState => Plan.Requests.Any(r => SdkReference.Get(r.Operation).MutatesRecoveryState) ||
        Plan.MessageSetAttributes?.Keys.Any(k => k is "newMessageSetID" or "oldMessageSetID") == true;
}
public static class SdkBatch
{
    public static bool IsSubscription(SdkPlan plan)=>plan.Requests.Count>0 && SdkReference.Get(plan.Requests[0].Operation).Transport=="subscription";
    public static PreparedSdkPlan Prepare(SdkPlan plan)
    {
        bool clearOnly=plan.Requests.Count==0 && plan.MessageSetAttributes?.ContainsKey("oldMessageSetID")==true && !plan.MessageSetAttributes.ContainsKey("newMessageSetID");
        if (plan.Requests.Count>100_000 || plan.Requests.Count==0 && !clearOnly || plan.BatchSize is < 1 or > 1000 || plan.MaximumBatchBytes is < 1024 or > 16_777_216)
            throw Usage("Invalid plan size/batch limits (1..100000 requests, 1..1000 per batch, 1024..16777216 bytes).");
        SdkXmlBuilder.ValidateProtocol(plan.ProtocolVersion,plan.Country);
        if (!clearOnly && plan.Requests.Select(r => SdkReference.Get(r.Operation).Transport).Distinct(StringComparer.Ordinal).Count() != 1)
            throw Usage("Company and subscription operations cannot share a plan.");
        bool subscription = IsSubscription(plan);
        foreach (var attribute in plan.MessageSetAttributes ?? new Dictionary<string,string>())
        {
            if (subscription) throw Usage("Subscription messages do not accept company message-set attributes.");
            if (attribute.Key == "responseData")
            {
                if (attribute.Value is not ("includeAll" or "includeNone")) throw Usage("responseData must be includeAll or includeNone.");
            }
            else if (attribute.Key is "newMessageSetID" or "oldMessageSetID")
            {
                if (string.IsNullOrWhiteSpace(attribute.Value) || attribute.Value.Length > 23 || attribute.Value.IndexOfAny([':', '/', '\\']) >= 0)
                    throw Usage("Message-set IDs must be nonempty, at most 23 characters and contain no colon or slash.");
                try { XmlConvert.VerifyXmlChars(attribute.Value); } catch (XmlException) { throw Usage("Message-set ID contains invalid XML characters."); }
            }
            else throw Usage("Unknown message-set attribute " + attribute.Key);
        }
        // Unlike QBXMLMsgsRq, QBXMLSubscriptionMsgsRq rejects onError on the
        // installed request processor. Submit one at a time to retain explicit
        // stopping/correlation semantics, while reusing the open connection.
        if (subscription) plan = plan with { BatchSize = 1 };
        if (plan.OnError is not ("stopOnError" or "continueOnError")) throw Usage("onError must be stopOnError or continueOnError.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var requests = new List<XElement>();
        foreach (var call in plan.Requests)
        {
            if (string.IsNullOrWhiteSpace(call.RequestId) || call.RequestId.Length > 100 || !ids.Add(call.RequestId)) throw Usage("Request IDs must be nonempty, unique and at most 100 characters.");
            var request = SdkXmlBuilder.Request(call.Operation, call.Fields, call.RequestId, plan.ProtocolVersion, plan.Country);
            if (Encoding.UTF8.GetByteCount(Message(plan, [request])) > plan.MaximumBatchBytes) throw Usage("An individual request exceeds the batch byte limit.");
            requests.Add(request);
        }
        SdkMacros.ValidatePlan(requests);
        var canonical = plan.Country + "\n" + plan.BatchSize + "\n" + plan.MaximumBatchBytes + "\n" + Message(plan, requests);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        bool recovery = plan.MessageSetAttributes?.Keys.Any(k => k is "newMessageSetID" or "oldMessageSetID") == true;
        var prepared = new PreparedSdkPlan(plan, requests, digest, recovery || plan.Requests.Any(r => SdkReference.Get(r.Operation).RequiresApply));
        if (recovery && Messages(prepared).Count != 1) throw Usage("Explicit recovery IDs require a single message set. Increase the batch bounds or supply separate plans with unique IDs.");
        return prepared;
    }
    public static string Message(SdkPlan plan, IEnumerable<XElement> requests)
    {
        bool subscription = IsSubscription(plan);
        return "<?xml version=\"1.0\"?><?qbxml version=\"" + plan.ProtocolVersion + "\"?>" +
            new XElement("QBXML", new XElement(subscription ? "QBXMLSubscriptionMsgsRq" : "QBXMLMsgsRq",
                subscription ? null : new XAttribute("onError", plan.OnError),
                plan.MessageSetAttributes?.Select(a => new XAttribute(a.Key,a.Value)), requests.Select(r => new XElement(r)))).ToString(SaveOptions.DisableFormatting);
    }
    public static IReadOnlyList<(int Start, string Xml)> Messages(PreparedSdkPlan prepared)
    {
        var result = new List<(int, string)>();
        if (prepared.Requests.Count==0) return [(0,Message(prepared.Plan,[]))];
        // Measure the nonempty envelope once, then each escaped UTF-8 request.
        // Re-serializing every growing candidate made large batches quadratic.
        var probe = new XElement("batch-size-probe");
        int envelopeBytes = Encoding.UTF8.GetByteCount(Message(prepared.Plan,[probe]))
            - Encoding.UTF8.GetByteCount(probe.ToString(SaveOptions.DisableFormatting));
        var requestBytes = prepared.Requests.Select(r=>Encoding.UTF8.GetByteCount(r.ToString(SaveOptions.DisableFormatting))).ToArray();
        int start = 0;
        while (start < prepared.Requests.Count)
        {
            var batch = new List<XElement>();
            int bytes = envelopeBytes;
            while (start + batch.Count < prepared.Requests.Count && batch.Count < prepared.Plan.BatchSize)
            {
                int next = requestBytes[start + batch.Count];
                if (bytes + next > prepared.Plan.MaximumBatchBytes) break;
                batch.Add(prepared.Requests[start + batch.Count]);
                bytes += next;
            }
            if (batch.Count == 0) throw Usage("Request exceeds byte limit.");
            result.Add((start, Message(prepared.Plan,batch))); start += batch.Count;
        }
        return result;
    }
    public static IReadOnlyList<SdkOutcome> Responses(string xml, IReadOnlyList<SdkCall> expected)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
            var document = XDocument.Load(reader);
            string envelope = expected.Count>0 && SdkReference.Get(expected[0].Operation).Transport == "subscription" ? "QBXMLSubscriptionMsgsRs" : "QBXMLMsgsRs";
            if (document.Root?.Name != "QBXML" || document.Root.Elements().Count() != 1 || document.Root.Element(envelope) is not { } messages) throw Invalid();
            var byId = expected.ToDictionary(c => c.RequestId, StringComparer.Ordinal);
            var parsed = new Dictionary<string, SdkOutcome>(StringComparer.Ordinal);
            foreach (var response in messages.Elements())
            {
                var id = (string?)response.Attribute("requestID");
                if (id is null || !byId.TryGetValue(id, out var call) || parsed.ContainsKey(id) || response.Name != SdkReference.Get(call.Operation).Response.Name) throw Invalid();
                if (!int.TryParse((string?)response.Attribute("statusCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)) throw Invalid();
                string? severity = (string?)response.Attribute("statusSeverity");
                if (severity is not ("Info" or "Warn" or "Error")) throw Invalid();
                var outcome = code == 3231 ? "unprocessed" : code == 0 && severity == "Info" ? "succeeded" : code == 1 && severity == "Info" && !response.Elements().Any() ? "no_match" : severity == "Warn" ? "warning" : "failed";
                parsed[id] = new(id, call.Operation, outcome, code, severity, (string?)response.Attribute("iteratorRemainingCount"), (string?)response.Attribute("iteratorID"), response.Elements().Select(Element).ToArray(), (string?)response.Attribute("statusMessage"));
            }
            return expected.Select(c => parsed.GetValueOrDefault(c.RequestId) ?? Unknown(c)).ToArray();
        }
        catch (XmlException) { throw Invalid(); }
    }
    private static SdkElement Element(XElement value) => new(value.Name.LocalName,
        value.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.Ordinal),
        value.HasElements ? null : value.Value, value.Elements().Select(Element).ToArray());
    private static IReadOnlyDictionary<string,string> MessageAttributes(string xml)
    {
        using var reader=XmlReader.Create(new StringReader(xml),new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=32*1024*1024});
        var document=XDocument.Load(reader);
        return document.Root!.Elements().Single().Attributes().ToDictionary(a=>a.Name.LocalName,a=>a.Value,StringComparer.Ordinal);
    }
    public static SdkOutcome Unknown(SdkCall call) => new(call.RequestId, call.Operation, "unknown", null, null, null, null, []);
    public static SdkExecutionResult Execute(PreparedSdkPlan prepared, string companyFile, string? journalPath, Func<string, string> process, string? requestedFileMode = null)
    {
        // CreateNew prevents accidentally appending a different plan to an old execution ledger.
        using var journal = journalPath is null ? null : new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        void Record(object value)
        {
            if (journal is null) return;
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n");
            journal.Write(bytes); journal.Flush(flushToDisk: true);
        }
        Record(new { kind = "plan", prepared.Digest, companyFile,
            sourceProcess = journal is null ? null : SdkJournalProcess.Current(),
            requestedFileMode,
            executionScope = IsSubscription(prepared.Plan) ? "local_quickbooks_installation" : "company",
            prepared.MutatesCompany, prepared.MutatesSubscriptions, prepared.MutatesRecoveryState, prepared.AffectsUserInterface,
            protocolVersion = prepared.Plan.ProtocolVersion, prepared.Plan.Country, prepared.Plan.OnError,
            prepared.Plan.BatchSize, prepared.Plan.MaximumBatchBytes, prepared.Plan.MessageSetAttributes, requests = prepared.Plan.Requests });
        var all = new List<SdkOutcome>();
        var messageSets = new List<SdkMessageSetResponse>();
        bool messageSetFailed = false;
        var batches = Messages(prepared);
        bool stopped = false;
        for (int i = 0; i < batches.Count; i++)
        {
            var batch = batches[i];
            int count = (i + 1 < batches.Count ? batches[i + 1].Start : prepared.Plan.Requests.Count) - batch.Start;
            var calls = prepared.Plan.Requests.Skip(batch.Start).Take(count).ToArray();
            if (stopped)
            {
                foreach (var call in calls)
                {
                    var outcome = new SdkOutcome(call.RequestId, call.Operation, "unprocessed", null, null, null, null, []);
                    Record(new { kind = "outcome", result = outcome }); all.Add(outcome);
                }
                continue;
            }
            Record(new { kind = "submitted", batch = i, requestIds = calls.Select(c => c.RequestId).ToArray(), requestXml = batch.Xml });
            IReadOnlyList<SdkOutcome> outcomes;
            try
            {
                var response = process(batch.Xml);
                outcomes = Responses(response, calls);
                var attributes = MessageAttributes(response);
                var metadata = new SdkMessageSetResponse(i,attributes);
                messageSets.Add(metadata); Record(new {kind="message_set_response",result=metadata});
                if (attributes.TryGetValue("messageSetStatusCode",out var status))
                {
                    if (!int.TryParse(status,NumberStyles.Integer,CultureInfo.InvariantCulture,out var code)) throw Invalid();
                    messageSetFailed |= code != 0;
                }
                else if (calls.Length==0) messageSetFailed=true; // No request statuses: clearing requires explicit SDK message-set acknowledgement.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // ProcessRequest can fail after QuickBooks has applied requests. Never retry automatically.
                outcomes = calls.Select(Unknown).ToArray();
                messageSetFailed=true;
                Record(new { kind = "uncertain_submission", batch = i, exceptionType = ex.GetType().Name, hresult = $"0x{ex.HResult:X8}" });
            }
            foreach (var outcome in outcomes) { Record(new { kind = "outcome", result = outcome }); all.Add(outcome); }
            stopped = messageSetFailed || outcomes.Any(o => o.Outcome is not ("succeeded" or "no_match"));
        }
        bool success = !messageSetFailed && all.All(o => o.Outcome is "succeeded" or "no_match");
        Record(new { kind = "finished", success, count = all.Count });
        bool subscription = IsSubscription(prepared.Plan);
        return new(success, 1, companyFile, prepared.Digest, prepared.MutatesCompany, false, journalPath, all,
            subscription ? "local_quickbooks_installation" : "company", prepared.MutatesSubscriptions,
            prepared.MutatesRecoveryState, messageSets, prepared.AffectsUserInterface, requestedFileMode);
    }
    private static QbException Usage(string message) => new("invalid_arguments", message);
    private static QbException Invalid() => new("invalid_response", "SDK batch responses could not be safely correlated. Submitted outcomes are uncertain.");
}
