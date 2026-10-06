using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using QbCli.Core;

namespace QbCli.QuickBooks;

public sealed record SdkJournalProcessIdentity(int ProcessId, string StartTimeUtc);

/// <summary>Observes the original process, distinguishing a reused PID from that process.</summary>
public static class SdkJournalProcess
{
    public static SdkJournalProcessIdentity Current()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.Id, process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    public static string Observe(SdkJournalProcessIdentity identity)
    {
        if (identity.ProcessId <= 0 || !DateTime.TryParseExact(identity.StartTimeUtc, "O", CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var started) || started.Kind != DateTimeKind.Utc)
            throw new QbException("invalid_arguments", "Journal process identity requires a positive PID and round-trip UTC start time.");
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime() != started) return "stopped";
            return "running";
        }
        catch (ArgumentException) { return "stopped"; } // No process currently has that PID.
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
        { return "unknown"; } // An observation failure is not evidence of exit.
    }
}
