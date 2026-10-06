using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using Microsoft.Win32;
using QbCli.Core;

namespace QbCli.QuickBooks;

public static class CallbackServerInput
{
    public static CallbackServerSettings Parse(JsonObject input)
    {
        if (input.Any(p => p.Key is not ("callbackClsid" or "durationSeconds" or "journalDirectory"))
            || input["callbackClsid"] is not JsonValue clsidValue || !clsidValue.TryGetValue<string>(out var text)
            || !Guid.TryParseExact(text, "B", out var clsid) || clsid == Guid.Empty
            || input["durationSeconds"] is not JsonValue duration || !duration.TryGetValue<int>(out var seconds) || seconds is < 1 or > 43200
            || input["journalDirectory"] is not JsonValue directoryValue || !directoryValue.TryGetValue<string>(out var directory)
            || string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new QbException("invalid_arguments", "Callback server settings require a fresh braced callbackClsid, durationSeconds 1..43200 and absolute journalDirectory; unknown fields are rejected.");
        return new(clsid, seconds, Path.GetFullPath(directory));
    }

    public static string Command(string executable, Guid clsid)
    {
        // Windows filenames cannot contain quotes. No shell is used by COM;
        // fixed switches plus a validated GUID keep configuration out of argv.
        if (clsid == Guid.Empty || !Path.IsPathFullyQualified(executable) || executable.Any(c => c == '"' || char.IsControl(c)))
            throw new QbException("invalid_arguments", "Callback server requires a nonempty identity and a valid absolute executable path.");
        return "\"" + executable + "\" --callback-clsid " + clsid.ToString("B");
    }
}

[SupportedOSPlatform("windows")]
public static class PersistentCallbackRegistration
{
    public const string Description = "McMurry Software qbcli auto-start event callback";
    private static string UserPath(Guid clsid) => "Software\\Classes\\CLSID\\" + clsid.ToString("B");

    public static CallbackServerRegistrationResult Register(CallbackServerSettings settings, string companyFile, string executable, bool apply)
    {
        Validate(settings, companyFile, executable);
        using var gate = new Mutex(false, "Local\\McMurrySoftware.qbcli.Callback." + settings.CallbackClsid.ToString("N"));
        bool owned;
        try { owned = gate.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new QbException("callback_identity_in_use", "A callback process already owns this identity.");
        try
        {
            using var existing = Registry.ClassesRoot.OpenSubKey("CLSID\\" + settings.CallbackClsid.ToString("B"));
            if (existing is not null) throw new QbException("callback_identity_in_use", "Callback CLSID is already registered. Use a fresh identity.");
            if (apply)
            {
                string ownership = Guid.NewGuid().ToString("N");
                using var key = Registry.CurrentUser.CreateSubKey(UserPath(settings.CallbackClsid), true);
                try
                {
                    key.SetValue("QbCliOwner", ownership);
                    key.SetValue("", Description);
                    key.SetValue("QbCliCompanyFile", Path.GetFullPath(companyFile));
                    key.SetValue("QbCliServerExecutable", Path.GetFullPath(executable));
                    key.SetValue("QbCliJournalDirectory", Path.GetFullPath(settings.JournalDirectory));
                    key.SetValue("QbCliDurationSeconds", settings.DurationSeconds, RegistryValueKind.DWord);
                    using var server = key.CreateSubKey("LocalServer32", true);
                    server.SetValue("", CallbackServerInput.Command(Path.GetFullPath(executable), settings.CallbackClsid));
                    server.SetValue("ServerExecutable", Path.GetFullPath(executable));
                    server.Flush(); key.Flush();
                }
                catch
                {
                    key.Close();
                    using (var failed = Registry.CurrentUser.OpenSubKey(UserPath(settings.CallbackClsid)))
                        if (failed?.GetValue("QbCliOwner") as string != ownership) throw;
                    Registry.CurrentUser.DeleteSubKeyTree(UserPath(settings.CallbackClsid), false);
                    throw;
                }
            }
            return new(true, 1, apply ? "apply" : "preview", apply, settings.CallbackClsid.ToString("B"), Path.GetFullPath(companyFile),
                Path.GetFullPath(executable), settings.JournalDirectory, settings.DurationSeconds, true, false, false, "current Windows user",
                "Experimental registration only. No SDK subscription or company request is made. Directly launched receiver callbacks passed synthetic checks; native COM auto-launch currently fails with 0x80040154 on the tested PC. This registration does not establish DeliverAlways support. Each receiver activation creates a new journal. Remove only your owned subscriptions, then clean the stopped registration deliberately.");
        }
        finally { gate.ReleaseMutex(); }
    }

    public static CallbackServerConfiguration Read(Guid clsid, string currentExecutable)
    {
        if (clsid == Guid.Empty) throw NotOwned();
        using var key = Registry.CurrentUser.OpenSubKey(UserPath(clsid));
        if (key is null || key.GetValue("") as string != Description || key.GetValue("QbCliOwner") is not string ownership || !Guid.TryParseExact(ownership, "N", out _)
            || key.GetValue("QbCliCompanyFile") is not string company || key.GetValue("QbCliServerExecutable") is not string executable
            || key.GetValue("QbCliJournalDirectory") is not string directory || key.GetValue("QbCliDurationSeconds") is not int seconds
            || !string.Equals(Path.GetFullPath(executable), Path.GetFullPath(currentExecutable), StringComparison.OrdinalIgnoreCase)) throw NotOwned();
        var settings = new CallbackServerSettings(clsid, seconds, directory);
        Validate(settings, company, executable);
        using var server = key.OpenSubKey("LocalServer32");
        if (server?.GetValue("") as string != CallbackServerInput.Command(executable, clsid) || server.GetValue("ServerExecutable") as string != executable) throw NotOwned();
        return new(settings, company, executable, ownership);
    }

    private static void Validate(CallbackServerSettings settings, string company, string executable)
    {
        _ = CallbackServerInput.Command(executable, settings.CallbackClsid);
        if (settings.DurationSeconds is < 1 or > 43200 || !Path.IsPathFullyQualified(settings.JournalDirectory) || !Directory.Exists(settings.JournalDirectory)
            || !Path.IsPathFullyQualified(company) || !string.Equals(Path.GetExtension(company), ".qbw", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new QbException("invalid_arguments", "Callback registration requires a company .QBW scope, existing server executable and existing absolute journal directory.");
    }
    private static QbException NotOwned() => new("callback_registration_not_owned", "The callback configuration is missing, malformed, changed or belongs to a different executable.");
}
