namespace QbCli.Core;

public sealed record EventNotification(string EventType, string CompanyFile, SdkElement Data);
public sealed record EventListenerResult(bool Success, int SchemaVersion, string CallbackClsid, string CompanyFile,
    string JournalFile, int ReceivedEvents, int IgnoredEvents, int FailedCallbacks, double ElapsedSeconds, bool TemporaryRegistration = false);
