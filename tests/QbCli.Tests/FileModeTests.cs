using System.Text.Json.Nodes;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class FileModeTests
{
    [Theory]
    [InlineData("any", CompanyFileMode.DoNotCare, 2)]
    [InlineData("single-user", CompanyFileMode.SingleUser, 0)]
    [InlineData("multi-user", CompanyFileMode.MultiUser, 1)]
    public void NamesRoundTripAndMapToSdkValues(string name, CompanyFileMode mode, int native)
    {
        Assert.Equal(mode, Options.Parse(["company", "info", "--file-mode", name]).FileMode);
        Assert.Equal(name, CompanyFileModes.Name(mode));
        Assert.Equal(native, SdkFileModes.NativeValue(mode));
    }

    [Fact] public void DefaultAcceptsExistingMode() =>
        Assert.Equal(CompanyFileMode.DoNotCare, Options.Parse(["company", "info"]).FileMode);

    [Theory]
    [InlineData("single")]
    [InlineData("Single-User")]
    [InlineData("0")]
    public void UnknownNamesFailBeforeConnecting(string name) =>
        Assert.Equal("invalid_arguments", Assert.Throws<QbException>(() => Options.Parse(["company", "info", "--file-mode", name])).Code);

    [Fact] public void MissingAndDuplicateArgumentsFail()
    {
        Assert.Throws<QbException>(() => Options.Parse(["company", "info", "--file-mode"]));
        Assert.Throws<QbException>(() => Options.Parse(["company", "info", "--file-mode", "any", "--file-mode", "single-user"]));
        Assert.Throws<QbException>(() => SdkFileModes.NativeValue((CompanyFileMode)100));
    }

    [Theory]
    [InlineData("help")]
    [InlineData("diagnostics")]
    [InlineData("capabilities")]
    [InlineData("sdk schema ClassQuery")]
    [InlineData("sdk validate ClassQuery")]
    [InlineData("sdk subscription SubscriptionDel")]
    [InlineData("sdk subscription-batch")]
    [InlineData("events listen")]
    [InlineData("events cleanup")]
    public void NonCompanyCommandsRejectMode(string command) =>
        Assert.Throws<QbException>(() => Options.Parse([.. command.Split(' '), "--file-mode", "any"]));

    [Fact] public void LiveDiscoveryAcceptsMode() =>
        Assert.Equal(CompanyFileMode.MultiUser, Options.Parse(["capabilities", "--live", "--file-mode", "multi-user"]).FileMode);

    [Theory]
    [InlineData(CompanyFileMode.DoNotCare)]
    [InlineData(CompanyFileMode.SingleUser)]
    [InlineData(CompanyFileMode.MultiUser)]
    public void ModeConfiguredAfterStableAccessBeforeBegin(CompanyFileMode mode)
    {
        var processor = new ModeProcessor();
        using (var reader = new QuickBooksReader(processor, "Test.QBW", fileMode: mode)) { }
        Assert.Equal(new[] { "open", "access", "mode:" + CompanyFileModes.Name(mode), "begin", "end", "close", "release" }, processor.Calls);
    }

    [Fact] public void ModeFailureClosesAndReleasesWithoutBegin()
    {
        var processor = new ModeProcessor { RejectMode = true };
        Assert.Throws<InvalidOperationException>(() => new QuickBooksReader(processor, "Test.QBW", fileMode: CompanyFileMode.SingleUser));
        Assert.Equal(new[] { "open", "access", "mode:single-user", "close", "release" }, processor.Calls);
    }

    [Fact] public void MutationPreviewReportsModeWithoutConnecting()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"ClassAdd\":{\"Name\":\"Synthetic\"}}");
            var output = new StringWriter();
            Assert.Equal(0, CliApplication.Run(["sdk", "run", "ClassAdd", "--input", path, "--file-mode", "single-user"],
                (_, _) => throw new Exception("Preview must not connect"), output, new StringWriter()));
            var preview = JsonNode.Parse(output.ToString())!;
            Assert.Equal("single-user", preview["requestedFileMode"]!.GetValue<string>());
            Assert.False(preview["submitted"]!.GetValue<bool>());
        }
        finally { File.Delete(path); }
    }

    [Fact] public void ExecutionAndDurableHeaderRetainRequestedMode()
    {
        var journal = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            var prepared = SdkBatch.Prepare(new("13.0", "US", "stopOnError", [new("query", "ClassQuery", new())]));
            var result = SdkBatch.Execute(prepared, "Test.QBW", journal,
                _ => "<QBXML><QBXMLMsgsRs><ClassQueryRs requestID='query' statusCode='0' statusSeverity='Info'/></QBXMLMsgsRs></QBXML>", "multi-user");
            Assert.Equal("multi-user", result.RequestedFileMode);
            var header = JsonNode.Parse(File.ReadLines(journal).First())!;
            Assert.Equal("multi-user", header["requestedFileMode"]!.GetValue<string>());
        }
        finally { File.Delete(journal); }
    }

    [Fact] public void DiscoveryDescribesRequestedVersusObservedMode()
    {
        var contract = CommandCatalog.Discover();
        Assert.NotNull(contract["options"]!["--file-mode"]);
        Assert.NotNull(contract["sdkConnectionContract"]!["requestedFileMode"]);
        Assert.Contains("HostQuery", contract["sdkConnectionContract"]!["observedFileMode"]!.GetValue<string>());
    }

    private sealed class ModeProcessor : IRequestProcessor
    {
        public List<string> Calls { get; } = [];
        public bool RejectMode;
        public void Open() => Calls.Add("open");
        public void ConfigureAccess() => Calls.Add("access");
        public void ConfigureFileMode(CompanyFileMode mode)
        {
            Calls.Add("mode:" + CompanyFileModes.Name(mode));
            if (RejectMode) throw new InvalidOperationException("Rejected mode");
        }
        public string Begin(string companyFile) { Calls.Add("begin"); return "ticket"; }
        public string Process(string ticket, string request) => throw new NotImplementedException();
        public string CurrentCompanyFile(string ticket) => "Test.QBW";
        public void End(string ticket) => Calls.Add("end");
        public void Close() => Calls.Add("close");
        public void Dispose() => Calls.Add("release");
    }
}
