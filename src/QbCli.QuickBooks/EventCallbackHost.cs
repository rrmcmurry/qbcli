using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using QbCli.Core;

namespace QbCli.QuickBooks;

// ABI facts verified against the installed official sdkevent.dll type library:
// IQBEventCallback, IUnknown-based, method1 inform([in] BSTR), HRESULT return.
[ComVisible(true),Guid("A3A4E33E-E747-48CF-947B-93FDE67FF72F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IQbEventCallback
{
    [PreserveSig] int Inform([MarshalAs(UnmanagedType.BStr)] string eventXml);
}
[ComVisible(true),Guid("00000001-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEventClassFactory
{
    [PreserveSig] int CreateInstance(nint outer,ref Guid iid,out nint instance);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
}
[ComVisible(true),ClassInterface(ClassInterfaceType.None)]
public sealed class EventCallback(EventJournal journal) : IQbEventCallback
{
    public int Inform(string eventXml)=>journal.Receive(eventXml);
}
[ComVisible(true),ClassInterface(ClassInterfaceType.None)]
[SupportedOSPlatform("windows")]
public sealed class EventClassFactory(EventCallback callback) : IEventClassFactory
{
    public int CreateInstance(nint outer,ref Guid iid,out nint instance)
    {
        instance=0;
        if(outer!=0) return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
        try
        {
            if(iid==typeof(IQbEventCallback).GUID) instance=Marshal.GetComInterfaceForObject(callback,typeof(IQbEventCallback));
            else if(iid==new Guid("00000000-0000-0000-C000-000000000046")) instance=Marshal.GetIUnknownForObject(callback);
            else return unchecked((int)0x80004002);
            return 0;
        }
        catch {return unchecked((int)0x80004005);}
    }
    public int LockServer(bool locked)=>0;
}

[SupportedOSPlatform("windows")]
public static class EventCallbackHost
{
    public static EventListenerResult RunPersistent(Guid clsid)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new QbException("invalid_runtime", "Event callbacks require one STA thread.");
        if (clsid == Guid.Empty) throw new QbException("invalid_arguments", "A callback identity is required.");
        using var gate = new Mutex(false, "Local\\McMurrySoftware.qbcli.Callback." + clsid.ToString("N"));
        bool owned;
        try { owned = gate.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new QbException("callback_identity_in_use", "A receiver already owns this callback identity.");
        try
        {
            // Resolve only the owned current-user registration for this exact
            // executable, under the same mutex used by registration/cleanup.
            var config = PersistentCallbackRegistration.Read(clsid, Environment.ProcessPath!);
            string journal = Path.Combine(config.Settings.JournalDirectory, "callback-" + clsid.ToString("N") + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
            return RunOwned(clsid, config.CompanyFile, journal, config.Settings.DurationSeconds, false, persistent: true);
        }
        finally { gate.ReleaseMutex(); }
    }
    public static EventListenerResult Run(Guid clsid,string companyFile,string journalFile,int seconds,bool registerCallback=false)
    {
        if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA) throw new QbException("invalid_runtime","Event callbacks require one STA thread.");
        if(clsid==Guid.Empty || seconds is <1 or >43200) throw new QbException("invalid_arguments","Listener requires a nonempty CLSID and durationSeconds 1..43200.");
        using var gate=new Mutex(false,"Local\\McMurrySoftware.qbcli.Callback."+clsid.ToString("N"));
        bool owned;
        try {owned=gate.WaitOne(0);} catch(AbandonedMutexException) {owned=true;}
        if(!owned) throw new QbException("callback_identity_in_use","A listener already owns this callback CLSID.");
        try {return RunOwned(clsid,companyFile,journalFile,seconds,registerCallback);}
        finally {gate.ReleaseMutex();}
    }
    private static EventListenerResult RunOwned(Guid clsid,string companyFile,string journalFile,int seconds,bool registerCallback,bool persistent=false)
    {
        if(!registerCallback && !persistent)
        {
            using var existing=Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("CLSID\\"+clsid.ToString("B"));
            if(existing is not null) throw new QbException("callback_identity_in_use","Listener must use a fresh CLSID, not an existing COM application's identity.");
        }
        using var registration=registerCallback ? new TemporaryCallbackRegistration(clsid,Environment.ProcessPath!,companyFile) : null;
        Marshal.ThrowExceptionForHR(CoInitializeEx(0,2));
        uint cookie=0;
        try
        {
            // The COM running-class table is process-scoped; no persistent registry
            // changes or subscriptions are created by listening.
            using var journal=new EventJournal(journalFile,companyFile,clsid.ToString("B"));
            var factory=new EventClassFactory(new EventCallback(journal));
            nint pointer=Marshal.GetComInterfaceForObject(factory,typeof(IEventClassFactory));
            try {Marshal.ThrowExceptionForHR(CoRegisterClassObject(ref clsid,pointer,4,1,out cookie));}
            finally {Marshal.Release(pointer);}
            journal.Ready();
            var watch=Stopwatch.StartNew();
            try
            {
                while(watch.Elapsed.TotalSeconds<seconds)
                {
                    while(PeekMessage(out var message,0,0,0,1)) {TranslateMessage(ref message);DispatchMessage(ref message);}
                    MsgWaitForMultipleObjects(0,0,false,50,0x04FF);
                }
            }
            finally {CoRevokeClassObject(cookie);cookie=0;GC.KeepAlive(factory);}
            journal.Complete();
            return new(journal.Failed==0,1,clsid.ToString("B"),companyFile,journalFile,journal.Received,journal.Ignored,journal.Failed,watch.Elapsed.TotalSeconds,registerCallback);
        }
        finally {if(cookie!=0) CoRevokeClassObject(cookie);CoUninitialize();}
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message {public nint Window;public uint Id;public nuint WParam;public nint LParam;public uint Time;public int X;public int Y;public uint Private;}
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved,uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoRegisterClassObject(ref Guid clsid,nint factory,uint context,uint flags,out uint cookie);
    [DllImport("ole32.dll")] private static extern int CoRevokeClassObject(uint cookie);
    [DllImport("user32.dll",EntryPoint="PeekMessageW")][return:MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out Message message,nint window,uint min,uint max,uint flags);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll",EntryPoint="DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjects(uint count,nint handles,[MarshalAs(UnmanagedType.Bool)] bool all,uint milliseconds,uint mask);
}
