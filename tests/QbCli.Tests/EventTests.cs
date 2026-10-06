using System.Text.Json.Nodes;
using QbCli;
using QbCli.QuickBooks;
using QbCli.Core;
using Xunit;

namespace QbCli.Tests;

public class EventTests
{
    private static string Notification(string path="C:\\test\\Test.QBW")=>$"<QBXML><QBXMLEvents><DataEvent><CompanyFilePath>{path}</CompanyFilePath><HostInfo><ProductName>QuickBooks</ProductName></HostInfo><ListEvent><ListEventType>Class</ListEventType><ListEventOperation>Add</ListEventOperation><ListID>123</ListID></ListEvent></DataEvent></QBXMLEvents></QBXML>";
    [Fact] public void ParsesActualCallbackEnvelopeAndPreservesFields()
    {
        var item=EventNotifications.Parse(Notification()).Single();
        Assert.Equal("DataEvent",item.EventType); Assert.Equal("C:\\test\\Test.QBW",item.CompanyFile);
        Assert.Equal("123",item.Data.Children.Single(c=>c.Name=="ListEvent").Children.Single(c=>c.Name=="ListID").Value);
    }
    [Theory]
    [InlineData("<!DOCTYPE QBXML [<!ENTITY x SYSTEM 'file:///private'>]><QBXML><QBXMLEvents>&x;</QBXMLEvents></QBXML>")]
    [InlineData("<QBXML><QBXMLMsgsRq/></QBXML>")]
    [InlineData("<QBXML><QBXMLEvents><DataEvent/></QBXMLEvents></QBXML>")]
    [InlineData("<QBXML><QBXMLEvents><Unknown><CompanyFilePath>C:\\test\\Test.QBW</CompanyFilePath></Unknown></QBXMLEvents></QBXML>")]
    [InlineData("<QBXML><QBXMLEvents/></QBXML>")]
    public void RejectsNonNotificationAndUnsafePayloads(string xml)=>Assert.Throws<QbException>(()=>EventNotifications.Parse(xml));
    [Fact] public void JournalFiltersOtherCompaniesAndAcknowledgesOnlyDurableAcceptedEvents()
    {
        var path=Path.GetTempFileName();File.Delete(path);
        try
        {
            using(var journal=new EventJournal(path,"C:\\test\\Test.QBW",Guid.NewGuid().ToString("B")))
            {
                journal.Ready(); Assert.Equal(0,journal.Receive(Notification()));
                Assert.Equal(0,journal.Receive(Notification("C:\\other\\Production.QBW")));
                Assert.NotEqual(0,journal.Receive("<invalid/>"));
                Assert.Equal(1,journal.Received);Assert.Equal(1,journal.Ignored);Assert.Equal(1,journal.Failed);
                journal.Complete();
            }
            var lines=File.ReadAllLines(path);
            Assert.Equal(4,lines.Length);Assert.DoesNotContain("Production",File.ReadAllText(path));
            Assert.Equal("event",JsonNode.Parse(lines[2])!["kind"]!.GetValue<string>());
        }
        finally {File.Delete(path);}
    }
    [Fact] public void ListenerArgumentsDoNotPermitSDKExecutionIntent()
    {
        Assert.Throws<QbException>(()=>Options.Parse(["events","listen","--input","settings.json"]));
        Assert.Throws<QbException>(()=>Options.Parse(["events","listen","--input","settings.json","--company-file","C:\\test\\Test.QBW","--journal","events.jsonl","--protocol","13.0"]));
    }
    [Fact] public void ExcessiveNestedCallbackFieldsAreRejected()
    {
        string inner=string.Concat(Enumerable.Repeat("<Nested>",70))+"x"+string.Concat(Enumerable.Repeat("</Nested>",70));
        Assert.Throws<QbException>(()=>EventNotifications.Parse(Notification().Replace("<ListID>123</ListID>",inner)));
    }
    [Fact] public void RegistrationPreviewNeverStartsSDKOrListener()
    {
        var file=Path.GetTempFileName();
        try
        {
            File.WriteAllText(file,"{\"callbackClsid\":\"{11223344-1234-4321-ABCD-1234567890AB}\",\"durationSeconds\":1,\"registerCallback\":true}");
            var output=new StringWriter();
            Assert.Equal(0,CliApplication.Run(["events","listen","--input",file,"--company-file","C:\\test\\Test.QBW","--journal","never-created.jsonl"],(_,_)=>throw new Exception("Must not connect"),output,new StringWriter()));
            Assert.Equal("preview",JsonNode.Parse(output.ToString())!["mode"]!.GetValue<string>());
            Assert.False(JsonNode.Parse(output.ToString())!["submitted"]!.GetValue<bool>());
        }
        finally {File.Delete(file);}
    }
    [Fact] public void TemporaryRegistrationRemovesOnlyItsOwnedFreshKey()
    {
        if(!OperatingSystem.IsWindows()) return;
        var clsid=Guid.NewGuid();string key="Software\\Classes\\CLSID\\"+clsid.ToString("B");
        using(var registration=new TemporaryCallbackRegistration(clsid,Environment.ProcessPath!,"C:\\test\\Test.QBW"))
        {
            using var present=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key);
            Assert.NotNull(present);Assert.NotNull(present.GetValue("QbCliOwner"));
            Assert.Throws<QbException>(()=> {if(OperatingSystem.IsWindows()) return new TemporaryCallbackRegistration(clsid,Environment.ProcessPath!,"C:\\test\\Test.QBW");throw new InvalidOperationException();});
        }
        using var absent=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key);
        Assert.Null(absent);
    }
    [Fact] public void CleanupPreviewsThenRemovesOwnedRegistrationAndRejectsWrongCompany()
    {
        if(!OperatingSystem.IsWindows()) return;
        var clsid=Guid.NewGuid();string path="Software\\Classes\\CLSID\\"+clsid.ToString("B");
        using var registration=new TemporaryCallbackRegistration(clsid,Environment.ProcessPath!,"C:\\test\\Test.QBW");
        Assert.Throws<QbException>(()=> {if(OperatingSystem.IsWindows()) return TemporaryCallbackRegistration.Cleanup(clsid,"C:\\other\\Other.QBW",true);throw new InvalidOperationException();});
        var preview=System.Text.Json.JsonSerializer.SerializeToNode(TemporaryCallbackRegistration.Cleanup(clsid,"C:\\test\\Test.QBW",false))!;
        Assert.False(preview["removed"]!.GetValue<bool>());
        using(var stillPresent=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path)) Assert.NotNull(stillPresent);
        var applied=System.Text.Json.JsonSerializer.SerializeToNode(TemporaryCallbackRegistration.Cleanup(clsid,"C:\\test\\Test.QBW",true))!;
        Assert.True(applied["removed"]!.GetValue<bool>());
        using var absent=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);Assert.Null(absent);
    }
}
