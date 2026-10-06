using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using QbCli.Core;

namespace QbCli.QuickBooks;

public sealed record SdkResumePlan(string CompanyFile, string SourceDigest, SdkPlan PendingPlan,
    IReadOnlyList<string> ConfirmedCompleted, IReadOnlyList<string> FailedExcluded, IReadOnlyList<string> RequiresReconciliation,
    SdkJournalProcessIdentity? SourceProcess = null, string SourceProcessState = "legacy_unknown")
{
    public bool CanApply => RequiresReconciliation.Count == 0 && SourceProcessState is not ("running" or "unknown");
}

/// <summary>Resubmit only requests proven not executed; never infer unknown writes failed.</summary>
public static class SdkResume
{
    public const int MaximumJournalRecordBytes = 256 * 1024 * 1024;
    public static SdkResumePlan Read(string path, int batchSize = 50)
    {
        try
        {
            // An incomplete JSON line is rejected, including a damaged final record.
            // Execution history can exceed the typed input size. Keep only the
            // current record and correlation state, not the whole journal text.
            return Build(File.ReadLines(path), batchSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { throw Usage("Resume journal could not be read as complete JSONL records."); }
    }
    public static SdkResumePlan Build(IEnumerable<string> lines, int batchSize = 50)
    {
        using var records = lines.GetEnumerator();
        if (!records.MoveNext()) throw Usage("Resume requires a nonempty complete journal.");
        JsonObject Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) throw Usage("Resume requires a nonempty complete journal.");
            if (line.Length > MaximumJournalRecordBytes || Encoding.UTF8.GetByteCount(line) > MaximumJournalRecordBytes)
                throw Usage("Resume journal record exceeds the 256 MiB limit.");
            using var document = JsonDocument.Parse(line);
            void Unique(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    foreach(var property in element.EnumerateObject()) { if (!keys.Add(property.Name)) throw Usage("Duplicate journal properties."); Unique(property.Value); }
                }
                else if (element.ValueKind == JsonValueKind.Array) foreach(var child in element.EnumerateArray()) Unique(child);
            }
            Unique(document.RootElement);
            return JsonNode.Parse(line) as JsonObject ?? throw Usage("Journal records must be objects.");
        }
        string Text(JsonObject value, string key) => value[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw Usage("Journal lacks required string " + key);
        int Number(JsonObject value, string key) => value[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : throw Usage("Journal lacks required integer " + key);
        var header = Parse(records.Current);
        if (Text(header,"kind") != "plan" || Text(header,"executionScope") != "company") throw Usage("Resume supports current company-scoped SDK journals only.");
        SdkJournalProcessIdentity? sourceProcess = null;
        string sourceProcessState = "legacy_unknown";
        if (header.ContainsKey("sourceProcess"))
        {
            if (header["sourceProcess"] is not JsonObject owner || owner.Count != 2)
                throw Usage("Journal process identity is malformed.");
            sourceProcess = new(Number(owner,"processId"), Text(owner,"startTimeUtc"));
            sourceProcessState = SdkJournalProcess.Observe(sourceProcess);
        }
        if (header["requests"] is not JsonArray requests) throw Usage("Journal plan lacks requests.");
        var calls = requests.Select(node => node is JsonObject call && call["fields"] is JsonObject fields
            ? new SdkCall(Text(call,"requestId"),Text(call,"operation"),(JsonObject)fields.DeepClone()) : throw Usage("Invalid journal request.")).ToArray();
        if (header["messageSetAttributes"] is JsonObject attributes && attributes.Any(p => p.Key is "newMessageSetID" or "oldMessageSetID"))
            throw Usage("Recovery-ID journals require deliberate SDK reconciliation, not residual-plan resume.");
        var messageAttributes = header["messageSetAttributes"] is JsonObject attrs ? attrs.ToDictionary(p => p.Key,p => Text(attrs,p.Key),StringComparer.Ordinal) : null;
        var original = new SdkPlan(Text(header,"protocolVersion"),Text(header,"country"),Text(header,"onError"),calls,
            Number(header,"batchSize"),Number(header,"maximumBatchBytes"),messageAttributes);
        var prepared = SdkBatch.Prepare(original);
        string digest = Text(header,"digest");
        if (prepared.Digest != digest) throw Usage("Journal plan digest does not match its typed requests and limits.");
        var byId = calls.ToDictionary(c => c.RequestId,StringComparer.Ordinal);
        var submitted = new HashSet<string>(StringComparer.Ordinal);
        var outcomes = new Dictionary<string,string>(StringComparer.Ordinal);
        var uncertainBatches = new HashSet<int>();
        var batches = SdkBatch.Messages(prepared);
        int submittedBatch = -1;
        bool finished = false;
        while(records.MoveNext())
        {
            var record = Parse(records.Current);
            string kind = Text(record,"kind");
            if (finished)
            {
                // Bulk read-back metadata follows the accounting execution ledger.
                if (kind == "reclassification_verification") continue;
                throw Usage("Journal contains execution records after finish.");
            }
            switch(kind)
            {
                case "submitted":
                    int index = Number(record,"batch");
                    if (index != submittedBatch + 1 || index >= batches.Count || Text(record,"requestXml") != batches[index].Xml)
                        throw Usage("Journal submission does not match the next internally generated batch.");
                    int end = index+1 < batches.Count ? batches[index+1].Start : calls.Length;
                    var expected = calls.Skip(batches[index].Start).Take(end-batches[index].Start).Select(c => c.RequestId).ToArray();
                    if (record["requestIds"] is not JsonArray ids || !ids.Select(n => n?.GetValue<string>()).SequenceEqual(expected)) throw Usage("Journal submission IDs differ from its batch.");
                    foreach(var submittedId in expected) submitted.Add(submittedId);
                    submittedBatch = index;
                    break;
                case "outcome":
                    if (record["result"] is not JsonObject result) throw Usage("Journal outcome lacks result.");
                    string id = Text(result,"requestId"), outcome = Text(result,"outcome");
                    if (!byId.TryGetValue(id,out var call) || call.Operation != Text(result,"operation") || !outcomes.TryAdd(id,outcome)) throw Usage("Invalid or duplicate journal outcome identity.");
                    if (outcome is not ("succeeded" or "no_match" or "unprocessed" or "failed" or "warning" or "unknown")) throw Usage("Unknown journal outcome.");
                    if (!submitted.Contains(id) && outcome != "unprocessed") throw Usage("Journal claims an outcome without submission intent.");
                    if (outcome == "unprocessed" && submitted.Contains(id) && Number(result,"statusCode") != 3231) throw Usage("Submitted unprocessed requests require SDK acknowledgement 3231.");
                    break;
                case "message_set_response":
                    if (record["result"] is not JsonObject envelope || Number(envelope,"batchIndex") != submittedBatch) throw Usage("Invalid message-set response metadata.");
                    if (envelope["attributes"] is not JsonObject metadata) throw Usage("Invalid message-set attributes.");
                    if (metadata.ContainsKey("messageSetStatusCode") && Text(metadata,"messageSetStatusCode") != "0") uncertainBatches.Add(submittedBatch);
                    break;
                case "uncertain_submission":
                    if (Number(record,"batch") != submittedBatch) throw Usage("Invalid uncertain submission batch.");
                    uncertainBatches.Add(submittedBatch);
                    break;
                case "finished": finished = true; break;
                default: throw Usage("Unknown journal record kind.");
            }
        }
        var completed = calls.Where(c => outcomes.GetValueOrDefault(c.RequestId) is "succeeded" or "no_match").Select(c => c.RequestId).ToArray();
        var failed = calls.Where(c => outcomes.GetValueOrDefault(c.RequestId) == "failed").Select(c => c.RequestId).ToArray();
        var envelopeUncertain = uncertainBatches.SelectMany(index => calls.Skip(batches[index].Start).Take((index+1 < batches.Count ? batches[index+1].Start : calls.Length)-batches[index].Start)).Select(c=>c.RequestId).ToHashSet(StringComparer.Ordinal);
        var uncertain = calls.Where(c => envelopeUncertain.Contains(c.RequestId) || outcomes.GetValueOrDefault(c.RequestId) is "warning" or "unknown" || submitted.Contains(c.RequestId) && !outcomes.ContainsKey(c.RequestId)).Select(c => c.RequestId).ToArray();
        var pending = calls.Where(c => !submitted.Contains(c.RequestId) && !outcomes.ContainsKey(c.RequestId) || outcomes.GetValueOrDefault(c.RequestId) == "unprocessed").ToArray();
        var residual = original with { Requests = pending, BatchSize = batchSize };
        if (pending.Length > 0)
        {
            // A wholly unexecuted chain can define fresh macros in the new
            // session. References to completed/failed definitions cannot.
            try { SdkBatch.Prepare(residual); }
            catch (QbException) when (pending.Any(c => HasMacro(c.Fields)))
            { throw Usage("Pending macro chain is not self-contained in the new session. References to prior completed or failed definitions require confirmed actual IDs in a separate plan."); }
        }
        return new(Text(header,"companyFile"),digest,residual,completed,failed,uncertain,sourceProcess,sourceProcessState);
    }
    private static bool HasMacro(JsonNode? value) => value switch
    {
        JsonObject obj => obj.Any(p => p.Key is "useMacro" or "defMacro" || HasMacro(p.Value)),
        JsonArray array => array.Any(HasMacro), _ => false
    };
    private static QbException Usage(string message) => new("invalid_arguments",message);
}
