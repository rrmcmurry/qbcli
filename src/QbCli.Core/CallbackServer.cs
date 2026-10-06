namespace QbCli.Core;

public sealed record CallbackServerSettings(Guid CallbackClsid, int DurationSeconds, string JournalDirectory);
public sealed record CallbackServerConfiguration(CallbackServerSettings Settings, string CompanyFile, string Executable, string Ownership);
public sealed record CallbackServerRegistrationResult(bool Success, int SchemaVersion, string Mode, bool Submitted,
    string CallbackClsid, string CompanyFile, string ServerExecutable, string JournalDirectory, int DurationSeconds,
    bool PersistentRegistration, bool AutoActivationVerified, bool QuickBooksDeliveryVerified, string Scope, string Guidance);
