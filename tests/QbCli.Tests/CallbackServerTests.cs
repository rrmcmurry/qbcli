using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using Microsoft.Win32;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class CallbackServerTests
{
    private static JsonObject Settings() => new()
    {
        ["callbackClsid"] = Guid.NewGuid().ToString("B"), ["durationSeconds"] = 10, ["journalDirectory"] = Path.GetTempPath()
    };

    [Fact] public void ParseKeepsBoundedIdentityAndDirectory()
    {
        var input = Settings(); var settings = CallbackServerInput.Parse(input);
        Assert.Equal(Guid.Parse(input["callbackClsid"]!.GetValue<string>()), settings.CallbackClsid);
        Assert.Equal(10, settings.DurationSeconds);
        Assert.True(Path.IsPathFullyQualified(settings.JournalDirectory));
    }

    [Theory] [InlineData(0)] [InlineData(43201)] [InlineData(-1)]
    public void InvalidDurationFails(int seconds)
    {
        var input = Settings(); input["durationSeconds"] = seconds; Assert.Throws<QbException>(() => CallbackServerInput.Parse(input));
    }

    [Fact] public void UnknownFieldsMalformedGuidAndRelativeDirectoryFail()
    {
        var extra = Settings(); extra["protocolVersion"] = "13.0"; Assert.Throws<QbException>(() => CallbackServerInput.Parse(extra));
        var identity = Settings(); identity["callbackClsid"] = Guid.Empty.ToString("B"); Assert.Throws<QbException>(() => CallbackServerInput.Parse(identity));
        var directory = Settings(); directory["journalDirectory"] = "relative"; Assert.Throws<QbException>(() => CallbackServerInput.Parse(directory));
        var number = Settings(); number["durationSeconds"] = "10"; Assert.Throws<QbException>(() => CallbackServerInput.Parse(number));
    }

    [Fact] public void ComCommandQuotesOnlyExecutableAndCarriesFixedGuidSwitch()
    {
        var clsid = Guid.NewGuid();
        Assert.Equal("\"C:\\Folder With Spaces\\qbcli-callback.exe\" --callback-clsid " + clsid.ToString("B"), CallbackServerInput.Command(@"C:\Folder With Spaces\qbcli-callback.exe", clsid));
        Assert.Throws<QbException>(() => CallbackServerInput.Command("C:\\bad\"path.exe", clsid));
        Assert.Throws<QbException>(() => CallbackServerInput.Command("C:\\bad\npath.exe", clsid));
    }

    [Fact, SupportedOSPlatform("windows")] public void PreviewCreatesNoKeyAndAppliedConfigurationPersistsAndRejectsOverwrite()
    {
        if (!OperatingSystem.IsWindows()) return;
        var settings = CallbackServerInput.Parse(Settings());
        string path = "Software\\Classes\\CLSID\\" + settings.CallbackClsid.ToString("B");
        try
        {
            var preview = PersistentCallbackRegistration.Register(settings, @"C:\Test.QBW", Environment.ProcessPath!, false);
            Assert.False(preview.Submitted);
            Assert.False(preview.AutoActivationVerified);
            Assert.False(preview.QuickBooksDeliveryVerified);
            using (var absent = Registry.CurrentUser.OpenSubKey(path)) Assert.Null(absent);
            var applied = PersistentCallbackRegistration.Register(settings, @"C:\Test.QBW", Environment.ProcessPath!, true);
            Assert.True(applied.Submitted);
            Assert.False(applied.AutoActivationVerified);
            Assert.False(applied.QuickBooksDeliveryVerified);
            var restored = PersistentCallbackRegistration.Read(settings.CallbackClsid, Environment.ProcessPath!);
            Assert.Equal(settings, restored.Settings); Assert.Equal(@"C:\Test.QBW", restored.CompanyFile);
            Assert.Throws<QbException>(() => PersistentCallbackRegistration.Register(settings, @"C:\Test.QBW", Environment.ProcessPath!, true));
            Assert.Throws<QbException>(() => PersistentCallbackRegistration.Read(settings.CallbackClsid, @"C:\Different.exe"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
    }

    [Fact, SupportedOSPlatform("windows")] public void ChangedServerCommandIsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;
        var settings = CallbackServerInput.Parse(Settings());
        string path = "Software\\Classes\\CLSID\\" + settings.CallbackClsid.ToString("B");
        try
        {
            PersistentCallbackRegistration.Register(settings, @"C:\Test.QBW", Environment.ProcessPath!, true);
            using (var server = Registry.CurrentUser.OpenSubKey(path + "\\LocalServer32", true)) server!.SetValue("", "changed");
            Assert.Equal("callback_registration_not_owned", Assert.Throws<QbException>(() => PersistentCallbackRegistration.Read(settings.CallbackClsid, Environment.ProcessPath!)).Code);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
    }

    [Fact, SupportedOSPlatform("windows")] public void CliRegistrationPreviewsAppliesAndCleansWithoutSdkConnection()
    {
        if (!OperatingSystem.IsWindows()) return;
        var input = Settings(); var file = Path.GetTempFileName();
        string clsid = input["callbackClsid"]!.GetValue<string>(); string path = "Software\\Classes\\CLSID\\" + clsid;
        try
        {
            File.WriteAllText(file, input.ToJsonString());
            var output = new StringWriter();
            Assert.Equal(0, CliApplication.Run(["events", "register", "--input", file, "--company-file", @"C:\Test.QBW"],
                (_, _) => throw new Exception("Must not connect"), output, new StringWriter(), callbackServerExecutable: () => Environment.ProcessPath!));
            Assert.False(JsonNode.Parse(output.ToString())!["submitted"]!.GetValue<bool>());
            using (var absent = Registry.CurrentUser.OpenSubKey(path)) Assert.Null(absent);
            output = new StringWriter();
            Assert.Equal(0, CliApplication.Run(["events", "register", "--input", file, "--company-file", @"C:\Test.QBW", "--apply"],
                (_, _) => throw new Exception("Must not connect"), output, new StringWriter(), callbackServerExecutable: () => Environment.ProcessPath!));
            Assert.True(JsonNode.Parse(output.ToString())!["submitted"]!.GetValue<bool>());
            Assert.Throws<QbException>(() => TemporaryCallbackRegistration.Cleanup(Guid.Parse(clsid), @"C:\Other.QBW", true));
            var preview = JsonSerializerNode(TemporaryCallbackRegistration.Cleanup(Guid.Parse(clsid), @"C:\Test.QBW", false));
            Assert.False(preview["removed"]!.GetValue<bool>());
            var cleanup = JsonSerializerNode(TemporaryCallbackRegistration.Cleanup(Guid.Parse(clsid), @"C:\Test.QBW", true));
            Assert.True(cleanup["removed"]!.GetValue<bool>());
            using var removed = Registry.CurrentUser.OpenSubKey(path); Assert.Null(removed);
        }
        finally { File.Delete(file); Registry.CurrentUser.DeleteSubKeyTree(path, false); }
    }

    private static JsonNode JsonSerializerNode(object value) => System.Text.Json.JsonSerializer.SerializeToNode(value)!;

    [Theory]
    [InlineData("events register --input settings.json")]
    [InlineData("events register --input settings.json --company-file C:\\Test.QBW --journal output.jsonl")]
    [InlineData("events register --input settings.json --company-file C:\\Test.QBW --file-mode any")]
    [InlineData("events register --input settings.json --company-file C:\\Test.QBW --protocol 13.0")]
    public void RegistrationRejectsCompanySessionAndJournalArguments(string command) => Assert.Throws<QbException>(() => Options.Parse(command.Split(' ')));
}
