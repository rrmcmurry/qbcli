using QbCli.QuickBooks;

namespace QbCli.CallbackServer;

internal static class Program
{
    // WinExe supplies no console window when COM starts the receiver. This
    // entry point never opens an SDK company session or installs a subscription.
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return 3;
        Guid? clsid = null;
        bool embedding = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--callback-clsid")
            {
                if (clsid is not null || ++i >= args.Length || !Guid.TryParseExact(args[i], "B", out var id) || id == Guid.Empty) return 2;
                clsid = id;
            }
            else if ((args[i].Equals("-Embedding", StringComparison.OrdinalIgnoreCase) || args[i].Equals("/Embedding", StringComparison.OrdinalIgnoreCase)) && !embedding)
                embedding = true;
            else return 2;
        }
        if (clsid is null) return 2;
        try { return EventCallbackHost.RunPersistent(clsid.Value).Success ? 0 : 4; }
        catch { return 4; } // Journal is the data channel; never show a modal UI.
    }
}
