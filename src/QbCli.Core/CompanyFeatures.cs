namespace QbCli.Core;

public sealed record CompanyFeatureFact(string Id, string SourcePath, bool? Enabled, string? Value,
    string State, string Reason);
public sealed record CompanyFeatureSnapshot(string ProbeState, string SourceOperation, string? ProtocolVersion,
    int? StatusCode, IReadOnlyList<CompanyFeatureFact> Facts, ErrorInfo? Error = null);
