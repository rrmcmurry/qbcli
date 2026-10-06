using System.Text.Json.Nodes;

namespace QbCli.Core;

public sealed record SdkCall(string RequestId, string Operation, JsonObject Fields);
public sealed record SdkPlan(string ProtocolVersion, string Country, string OnError, IReadOnlyList<SdkCall> Requests,
    int BatchSize = 50, int MaximumBatchBytes = 1_048_576, IReadOnlyDictionary<string, string>? MessageSetAttributes = null);
public sealed record SdkElement(string Name, IReadOnlyDictionary<string, string> Attributes, string? Value, IReadOnlyList<SdkElement> Children);
public sealed record SdkOutcome(string RequestId, string Operation, string Outcome, int? StatusCode, string? Severity,
    string? RemainingCount, string? IteratorId, IReadOnlyList<SdkElement> Data, string? StatusMessage = null);
public sealed record SdkExecutionResult(bool Success, int SchemaVersion, string CompanyFile, string PlanDigest,
    bool MutatesCompany, bool Atomic, string? JournalFile, IReadOnlyList<SdkOutcome> Results,
    string ExecutionScope = "company", bool MutatesSubscriptions = false, bool MutatesRecoveryState = false,
    IReadOnlyList<SdkMessageSetResponse>? MessageSetResponses = null, bool AffectsUserInterface = false, string? RequestedFileMode = null);
public sealed record SdkMessageSetResponse(int BatchIndex, IReadOnlyDictionary<string, string> Attributes);
public interface ISdkExecutor
{
    SdkExecutionResult ExecuteSdk(SdkPlan plan, string expectedCompanyFile, string? journalFile);
    SdkQueryResult QuerySdkAll(SdkPlan plan, string expectedCompanyFile, int pageSize);
}
public sealed record SdkQueryResult(bool Success, int SchemaVersion, string CompanyFile, string Operation, string ProtocolVersion,
    bool Complete, int Pages, IReadOnlyList<SdkElement> Data, string? RequestedFileMode = null);
