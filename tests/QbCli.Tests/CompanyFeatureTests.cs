using System.Text.Json;
using QbCli;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class CompanyFeatureTests
{
    private static SdkElement E(string name,string? value=null,params SdkElement[] children)=>new(name,new Dictionary<string,string>(),value,children);
    private static readonly ConnectionInfo Connection=new(new("Test",null),new("Test host","34","0","US",["6.0","13.0","17.0","18.0"],false,"SingleUser"),@"C:\Test.QBW","6.0");
    private static CompanyFeatureFact Fact(CompanyFeatureSnapshot snapshot,string id)=>snapshot.Facts.Single(f=>f.Id==id);
    [Fact] public void EveryFeatureSelectorNamesAnImportedDocumentedField()
    {
        var schema=SdkReference.Get("PreferencesQuery").Response.Children.Single(n=>n.Name=="PreferencesRet");
        foreach(var fact in CompanyFeatureDiscovery.NotQueried().Facts)
        {
            var path=fact.SourcePath.Split('/');
            var group=schema.Children.Single(n=>n.Name==path[1]);
            var field=group.Children.Single(n=>n.Name==path[2]);
            Assert.Contains(field.Type,new[]{"BOOLTYPE","ENUMTYPE"});
        }
    }
    [Fact] public void UnsupportedHostStaysUnknownWithoutInvokingExecutor()
    {
        var executor=new Executor(_=>throw new Exception("Must not query unsupported host"));
        foreach(var host in new[]{Connection.Host with {Country="OE"},Connection.Host with {SupportedProtocolVersions=["18.0"]}})
        {
            var snapshot=CompanyFeatureDiscovery.Read(executor,Connection with {Host=host});
            Assert.Equal("unsupported_host",snapshot.ProbeState);
            Assert.All(snapshot.Facts,f=>Assert.Null(f.Enabled));
        }
        Assert.Null(executor.Plan);
    }
    [Fact] public void ExplicitLiveDiscoveryCompanyMismatchFailsBeforePreferences()
    {
        var reader=new Reader();var output=new StringWriter();
        Assert.Equal(4,CliApplication.Run(["capabilities","--live","--company-file",@"C:\Other.QBW"],(_,_)=>reader,output,new StringWriter()));
        Assert.Equal(0,reader.Calls);Assert.True(reader.Disposed);Assert.Contains("company_mismatch",output.ToString());
    }
    [Theory] [InlineData("true",true)] [InlineData("1",true)] [InlineData("false",false)] [InlineData("0",false)]
    public void KnownBooleansRemainConfigurationFacts(string value,bool enabled)
    {
        var snapshot=CompanyFeatureDiscovery.Parse(E("PreferencesRet",null,E("AccountingPreferences",null,E("IsUsingClassTracking",value))),"13.0");
        Assert.Equal(enabled,Fact(snapshot,"class_tracking").Enabled);
        Assert.Equal(enabled ? "enabled" : "disabled",Fact(snapshot,"class_tracking").State);
        Assert.Equal("not_returned",Fact(snapshot,"inventory").Reason);
        Assert.Null(Fact(snapshot,"inventory").Enabled);
    }
    [Theory] [InlineData("None",false)] [InlineData("SerialNumber",true)] [InlineData("LotNumber",true)]
    public void SerialAndLotEnumIsExplicit(string value,bool enabled)
    {
        var snapshot=CompanyFeatureDiscovery.Parse(E("PreferencesRet",null,E("ItemsAndInventoryPreferences",null,E("IsTrackingSerialOrLotNumber",value))),"13.0");
        Assert.Equal(enabled,Fact(snapshot,"serial_or_lot_tracking").Enabled);
        Assert.Equal(value,Fact(snapshot,"serial_or_lot_tracking").Value);
    }
    [Fact] public void DuplicatesAndMalformedValuesStayUnknownAndDoNotLeak()
    {
        var snapshot=CompanyFeatureDiscovery.Parse(E("PreferencesRet",null,
            E("AccountingPreferences",null,E("IsUsingClassTracking","true"),E("IsUsingClassTracking","false")),
            E("ItemsAndInventoryPreferences",null,E("IsTrackingSerialOrLotNumber","secret value")),
            E("CurrentAppAccessRights",null,E("AutomaticLoginUserName","private admin"),E("IsAutomaticLoginAllowed","private value")),
            E("MultiCurrencyPreferences",null,E("IsMultiCurrencyOn","true")),E("MultiCurrencyPreferences",null,E("IsMultiCurrencyOn","true"))),"13.0");
        Assert.Equal("duplicate_path",Fact(snapshot,"class_tracking").Reason);
        Assert.Equal("duplicate_path",Fact(snapshot,"multicurrency").Reason);
        Assert.Equal("invalid_value",Fact(snapshot,"serial_or_lot_tracking").Reason);
        Assert.Equal("invalid_value",Fact(snapshot,"app_automatic_login_allowed").Reason);
        Assert.All(snapshot.Facts,f=>Assert.Null(f.Enabled));
        var json=JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("private",json);Assert.DoesNotContain("secret",json);Assert.DoesNotContain("AutomaticLoginUserName",json);
    }
    [Fact] public void ProbeSelectsSupportedProtocolAndNeverRequestsWrites()
    {
        var executor=new Executor(_=>Success(E("PreferencesRet",null,E("PurchasesAndVendorsPreferences",null,E("IsUsingInventory","false")))));
        var snapshot=CompanyFeatureDiscovery.Read(executor,Connection);
        Assert.Equal("succeeded",snapshot.ProbeState);Assert.Equal("17.0",snapshot.ProtocolVersion);
        Assert.Equal("17.0",executor.Plan!.ProtocolVersion);Assert.Equal("PreferencesQuery",Assert.Single(executor.Plan.Requests).Operation);
        Assert.False(SdkBatch.Prepare(executor.Plan).RequiresApply);
        Assert.Equal(Connection.CompanyFile,executor.Company);Assert.Null(executor.Journal);
    }
    [Fact] public void OptionalSdkFailurePreservesUnknownFactsAndSanitizesStatusMessage()
    {
        var executor=new Executor(_=>new(false,1,Connection.CompanyFile,"digest",false,false,null,
            [new("preferences","PreferencesQuery","failed",3250,"Error",null,null,[],"private SDK explanation")]));
        var snapshot=CompanyFeatureDiscovery.Read(executor,Connection);
        Assert.Equal("failed",snapshot.ProbeState);Assert.Equal(3250,snapshot.StatusCode);
        Assert.All(snapshot.Facts,f=>Assert.Null(f.Enabled));
        Assert.DoesNotContain("private",JsonSerializer.Serialize(snapshot));
        var exception=new Executor(_=>throw new QbException("quickbooks_status","private exception","3250"));
        Assert.DoesNotContain("private",JsonSerializer.Serialize(CompanyFeatureDiscovery.Read(exception,Connection)));
    }
    [Fact] public void CompanyMismatchCannotBeDowngradedToOptionalProbeFailure()
    {
        var executor=new Executor(_=>Success(E("PreferencesRet")) with {CompanyFile=@"C:\Other.QBW"});
        Assert.Equal("company_mismatch",Assert.Throws<QbException>(()=>CompanyFeatureDiscovery.Read(executor,Connection)).Code);
        var changed=new Executor(_=>throw new QbException("company_mismatch","changed company"));
        Assert.Throws<QbException>(()=>CompanyFeatureDiscovery.Read(changed,Connection));
    }
    [Fact] public void OfflineDiscoveryDoesNotProbeAndLiveDiscoveryUsesSameReader()
    {
        var offline=CommandCatalog.Discover();
        Assert.Equal("not_queried",offline["companyFeatures"]!["probeState"]!.GetValue<string>());
        using var reader=new Reader();var output=new StringWriter();
        Assert.Equal(0,CliApplication.Run(["capabilities","--live","--company-file",Connection.CompanyFile],(_,_)=>reader,output,new StringWriter()));
        Assert.Equal(1,reader.Calls);Assert.True(reader.Disposed);
        using var parsed=JsonDocument.Parse(output.ToString());
        Assert.Equal("succeeded",parsed.RootElement.GetProperty("companyFeatures").GetProperty("probeState").GetString());
        Assert.All(parsed.RootElement.GetProperty("sdkCatalog").GetProperty("operations").EnumerateArray(),o=>Assert.Equal(JsonValueKind.Null,o.GetProperty("availableInConnectedCompany").ValueKind));
    }
    private static SdkExecutionResult Success(SdkElement record)=>new(true,1,Connection.CompanyFile,"digest",false,false,null,[new("preferences","PreferencesQuery","succeeded",0,"Info",null,null,[record])]);
    private sealed class Executor(Func<SdkPlan,SdkExecutionResult> callback):ISdkExecutor
    {
        public SdkPlan? Plan;public string? Company;public string? Journal;
        public SdkExecutionResult ExecuteSdk(SdkPlan plan,string company,string? journal){Plan=plan;Company=company;Journal=journal;return callback(plan);}
        public SdkQueryResult QuerySdkAll(SdkPlan plan,string company,int pageSize)=>throw new NotImplementedException();
    }
    private sealed class Reader:IBooksReader,ISdkExecutor
    {
        public bool Disposed;public int Calls;
        public ConnectionInfo GetConnectionInfo()=>Connection;
        public SdkExecutionResult ExecuteSdk(SdkPlan plan,string company,string? journal){Calls++;return Success(E("PreferencesRet"));}
        public SdkQueryResult QuerySdkAll(SdkPlan plan,string company,int pageSize)=>throw new NotImplementedException();
        public CompanyInfo GetCompany()=>Connection.Company;
        public IReadOnlyList<Account> GetAccounts()=>[];
        public IReadOnlyList<Party> GetParties(PartyKind kind,PartySelection selection,int pageSize)=>[];
        public void Dispose()=>Disposed=true;
    }
}
