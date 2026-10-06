using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class SessionTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void SuccessfulSessionUsesSameAccessProfileAndCorrectCleanup(bool allowWrites)
    {
        var processor = new FakeProcessor(); var reader = new QuickBooksReader(processor, "test.QBW", allowWrites: allowWrites);
        reader.GetAccounts(); reader.Dispose(); reader.Dispose();
        Assert.Equal(new[] { "open", "access", "begin:test.QBW", "query:ticket", "end:ticket", "close", "release" }, processor.Calls);
    }
    [Theory] [InlineData("open", "open,release")] [InlineData("access", "open,access,close,release")]
    [InlineData("begin", "open,access,begin:,close,release")]
    public void StartupFailuresReleaseResources(string stage, string expected)
    {
        var processor = new FakeProcessor { Failure = stage };
        Assert.Throws<InvalidOperationException>(() => new QuickBooksReader(processor, ""));
        Assert.Equal(expected, string.Join(',', processor.Calls));
    }
    [Fact] public void EndFailureStillClosesAndReleases()
    {
        var processor = new FakeProcessor { Failure = "end" }; var log = new List<string>();
        using (var reader = new QuickBooksReader(processor, "", log.Add)) { }
        Assert.Equal(new[] { "end:ticket", "close", "release" }, processor.Calls.TakeLast(3));
        Assert.Contains(log, x => x.Contains("EndSession failed"));
    }
    [Fact] public void QueryFailureStillCleansUp()
    {
        var processor = new FakeProcessor { Failure = "query" };
        Assert.Throws<InvalidOperationException>(() => { using var reader = new QuickBooksReader(processor, ""); reader.GetAccounts(); });
        Assert.Equal(new[] { "end:ticket", "close", "release" }, processor.Calls.TakeLast(3));
    }
    [Fact] public void CloseFailureStillReleases()
    {
        var processor = new FakeProcessor { Failure = "close" };
        using (var reader = new QuickBooksReader(processor, "")) { }
        Assert.Equal("release", processor.Calls.Last());
    }
    [Fact] public void DisposedReaderCannotQuery()
    {
        var reader = new QuickBooksReader(new FakeProcessor(), ""); reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.GetAccounts());
    }
    private sealed class FakeProcessor : IRequestProcessor
    {
        public List<string> Calls { get; } = [];
        public string? Failure;
        private void Call(string stage, string? detail = null)
        {
            Calls.Add(stage + detail);
            if (stage == Failure) throw new InvalidOperationException("test failure");
        }
        public void Open() => Call("open");
        public string CurrentCompanyFile(string ticket) => "test.QBW";
        public void ConfigureAccess() => Call("access");
        public string Begin(string companyFile) { Call("begin", ":" + companyFile); return "ticket"; }
        public string Process(string ticket, string request) { Call("query", ":" + ticket); return "<QBXML><QBXMLMsgsRs><AccountQueryRs statusCode='0' statusSeverity='Info'/></QBXMLMsgsRs></QBXML>"; }
        public void End(string ticket) => Call("end", ":" + ticket);
        public void Close() => Call("close");
        public void Dispose() => Call("release");
    }
}
