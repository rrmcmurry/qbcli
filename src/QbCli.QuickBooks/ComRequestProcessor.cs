using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using QbCli.Core;

namespace QbCli.QuickBooks;

// Adapter-only seam for offline lifecycle tests; never exposed as a CLI command.
public interface IRequestProcessor : IDisposable
{
    void Open();
    void ConfigureAccess();
    void ConfigureFileMode(CompanyFileMode mode)
    {
        if (mode != CompanyFileMode.DoNotCare) throw new QbException("unsupported_capability", "This request processor does not support selecting a company file mode.");
    }
    string Begin(string companyFile);
    string Process(string ticket, string request);
    string CurrentCompanyFile(string ticket);
    void End(string ticket);
    void Close();
}
public interface ISubscriptionProcessor
{
    string ProcessSubscription(string request);
}

// Narrow IDispatch projections of documented SDK interfaces. GUIDs, DISPIDs and enum
// values were checked against the installed Intuit QBXMLRP2 public COM type library.
// InterfaceIsIDispatch permits a subset; these are not hand-built vtable projections.
[ComImport, Guid("8DB91B17-D5A2-41DB-80A7-CC6F1E449A05"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IIntuitRequestProcessor4
{
    [DispId(13)] [return: MarshalAs(UnmanagedType.BStr)]
    string ProcessSubscription([MarshalAs(UnmanagedType.BStr)] string request);
    [DispId(1)] [return: MarshalAs(UnmanagedType.BStr)]
    string ProcessRequest([MarshalAs(UnmanagedType.BStr)] string ticket, [MarshalAs(UnmanagedType.BStr)] string request);
    [DispId(3)] void CloseConnection();
    [DispId(4)] [return: MarshalAs(UnmanagedType.BStr)]
    string BeginSession([MarshalAs(UnmanagedType.BStr)] string companyFile, int fileMode);
    [DispId(5)] void EndSession([MarshalAs(UnmanagedType.BStr)] string ticket);
    [DispId(6)] [return: MarshalAs(UnmanagedType.BStr)]
    string GetCurrentCompanyFileName([MarshalAs(UnmanagedType.BStr)] string ticket);
    [DispId(15)] void OpenConnection2([MarshalAs(UnmanagedType.BStr)] string appId, [MarshalAs(UnmanagedType.BStr)] string appName, int connectionType);
    [DispId(16)] IIntuitAuthPreferences AuthPreferences { [return: MarshalAs(UnmanagedType.Interface)] get; }
}
[ComImport, Guid("4277578A-9687-4EBC-8586-1CA4759B17C3"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IIntuitAuthPreferences
{
    [DispId(3)] void PutIsReadOnly([MarshalAs(UnmanagedType.VariantBool)] bool readOnly);
    [DispId(7)] void PutPersonalDataPref(int preference);
    [DispId(5)] void PutUnattendedModePref(int preference);
}

[SupportedOSPlatform("windows")]
public sealed class ComRequestProcessor : IRequestProcessor, ISubscriptionProcessor
{
    private object? instance;
    private IIntuitRequestProcessor4? processor;
    private readonly int threadId = Environment.CurrentManagedThreadId;
    private Mutex? sessionGate;
    private bool ownsGate;
    private CompanyFileMode fileMode;
    public ComRequestProcessor()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("QuickBooks COM requires an STA thread.");
        var type = Type.GetTypeFromProgID("QBXMLRP2.RequestProcessor", throwOnError: true)!;
        instance = Activator.CreateInstance(type)!;
        try { processor = (IIntuitRequestProcessor4)instance; }
        catch { Marshal.FinalReleaseComObject(instance); instance = null; throw; }
    }
    private IIntuitRequestProcessor4 Processor
    {
        get
        {
            if (Environment.CurrentManagedThreadId != threadId) throw new InvalidOperationException("COM session changed threads.");
            return processor ?? throw new ObjectDisposedException(nameof(ComRequestProcessor));
        }
    }
    public void Open()
    {
        sessionGate = new Mutex(false, @"Local\McMurrySoftware.qbcli.QuickBooksSDK");
        try { ownsGate = sessionGate.WaitOne(0); }
        catch (AbandonedMutexException) { ownsGate = true; }
        if (!ownsGate) throw new QbCli.Core.QbException("sdk_busy", "Another qbcli session is active. Reuse a batch rather than running concurrent SDK processes.");
        Processor.OpenConnection2("", "McMurry Software QuickBooks CLI", 1); // localQBD
    }
    public void ConfigureAccess()
    {
        var prefs = Processor.AuthPreferences;
        // Stable, full profile requested by the user: do not switch permissions
        // between query and mutation sessions. CLI apply intent remains separate.
        try { prefs.PutIsReadOnly(false); prefs.PutPersonalDataPref(1); prefs.PutUnattendedModePref(1); } // pdpRequired, umpRequired
        // Intuit may return preferences on the same COM identity/RCW as the
        // processor. Releasing that RCW here would disconnect the live processor.
        finally { if (!ReferenceEquals(prefs, instance)) Marshal.FinalReleaseComObject(prefs); }
    }
    public void ConfigureFileMode(CompanyFileMode mode)
    {
        _ = Processor; // Validate lifetime and STA ownership without opening a session.
        _ = SdkFileModes.NativeValue(mode);
        fileMode = mode;
    }
    public string Begin(string companyFile) => Processor.BeginSession(companyFile, SdkFileModes.NativeValue(fileMode));
    public string Process(string ticket, string request) => Processor.ProcessRequest(ticket, request);
    public string ProcessSubscription(string request) => Processor.ProcessSubscription(request);
    public string CurrentCompanyFile(string ticket) => Processor.GetCurrentCompanyFileName(ticket);
    public void End(string ticket) => Processor.EndSession(ticket);
    public void Close() => Processor.CloseConnection();
    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != threadId) throw new InvalidOperationException("COM release changed threads.");
        try { if (instance is not null) Marshal.FinalReleaseComObject(instance); }
        finally
        {
            processor = null;
            instance = null;
            if (ownsGate) { sessionGate!.ReleaseMutex(); ownsGate = false; }
            sessionGate?.Dispose(); sessionGate = null;
        }
    }
}
