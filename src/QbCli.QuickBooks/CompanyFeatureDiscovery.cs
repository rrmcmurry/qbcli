using System.Globalization;
using QbCli.Core;

namespace QbCli.QuickBooks;

/// <summary>Preference observations, not operation availability or permission proofs.</summary>
public static class CompanyFeatureDiscovery
{
    private static readonly (string Id,string Group,string Field)[] Flags = [
        ("class_tracking","AccountingPreferences","IsUsingClassTracking"),
        ("account_numbers","AccountingPreferences","IsUsingAccountNumbers"),
        ("estimates","JobsAndEstimatesPreferences","IsUsingEstimates"),
        ("progress_invoicing","JobsAndEstimatesPreferences","IsUsingProgressInvoicing"),
        ("multicurrency","MultiCurrencyPreferences","IsMultiCurrencyOn"),
        ("inventory","PurchasesAndVendorsPreferences","IsUsingInventory"),
        ("multi_location_inventory_available","MultiLocationInventoryPreferences","IsMultiLocationInventoryAvailable"),
        ("multi_location_inventory_enabled","MultiLocationInventoryPreferences","IsMultiLocationInventoryEnabled"),
        ("enhanced_inventory_receiving","ItemsAndInventoryPreferences","EnhancedInventoryReceivingEnabled"),
        ("fifo","ItemsAndInventoryPreferences","FIFOEnabled"),
        ("barcode","ItemsAndInventoryPreferences","IsBarcodeEnabled"),
        ("advanced_inventory_rsb","ItemsAndInventoryPreferences","IsRSBEnabled"),
        ("app_automatic_login_allowed","CurrentAppAccessRights","IsAutomaticLoginAllowed"),
        ("app_personal_data_access_allowed","CurrentAppAccessRights","IsPersonalDataAccessAllowed")];
    private const string SerialPath="PreferencesRet/ItemsAndInventoryPreferences/IsTrackingSerialOrLotNumber";
    public static CompanyFeatureSnapshot NotQueried(string state="not_queried",ErrorInfo? error=null)=>
        new(state,"PreferencesQuery",null,null,UnknownFacts(state),error);
    private static IReadOnlyList<CompanyFeatureFact> UnknownFacts(string reason)=>
        [..Flags.Select(f=>new CompanyFeatureFact(f.Id,"PreferencesRet/"+f.Group+"/"+f.Field,null,null,"unknown",reason)),
            new("serial_or_lot_tracking",SerialPath,null,null,"unknown",reason)];
    public static CompanyFeatureSnapshot Read(ISdkExecutor executor,ConnectionInfo connection)
    {
        string? country=connection.Host.Country;
        if(country is not ("US" or "CA" or "UK" or "AU"))return NotQueried("unsupported_host");
        var candidates=connection.Host.SupportedProtocolVersions.Where(v=>decimal.TryParse(v,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var n)
            && n>=1 && n<=decimal.Parse(SdkReference.MaximumProtocol,CultureInfo.InvariantCulture)).OrderByDescending(v=>decimal.Parse(v,CultureInfo.InvariantCulture));
        string? version=candidates.FirstOrDefault(v=>
        {
            try{SdkBatch.Prepare(new(v,country,"stopOnError",[new("preferences","PreferencesQuery",new())]));return true;}
            catch(QbException){return false;}
        });
        if(version is null)return NotQueried("unsupported_host");
        try
        {
            var result=executor.ExecuteSdk(new(version,country,"stopOnError",[new("preferences","PreferencesQuery",new())]),connection.CompanyFile,null);
            if(!string.Equals(Path.GetFullPath(result.CompanyFile),Path.GetFullPath(connection.CompanyFile),StringComparison.OrdinalIgnoreCase))
                throw new QbException("company_mismatch","The optional preferences response belongs to another company.");
            if(!result.Success || result.Results.Count!=1 || result.Results[0].RequestId!="preferences" || result.Results[0].Operation!="PreferencesQuery"
                || result.Results[0].Outcome!="succeeded" || result.Results[0].Data.Count!=1 || result.Results[0].Data[0].Name!="PreferencesRet")
                return new("failed","PreferencesQuery",version,result.Results.Count==1 ? result.Results[0].StatusCode : null,UnknownFacts("probe_failed"),
                    new("feature_probe_failed","The optional preferences probe did not return one successful preferences record."));
            return Parse(result.Results[0].Data[0],version,result.Results[0].StatusCode);
        }
        catch(QbException ex) when(ex.Code is not ("company_mismatch" or "session_failed"))
        {return new("failed","PreferencesQuery",version,null,UnknownFacts("probe_failed"),new(ex.Code,"The optional preferences probe did not complete.",ex.DiagnosticCode));}
        catch(System.Runtime.InteropServices.COMException ex)
        {return new("failed","PreferencesQuery",version,null,UnknownFacts("probe_failed"),new("quickbooks_com","The optional preferences probe did not complete.","0x"+ex.HResult.ToString("X8",CultureInfo.InvariantCulture)));}
    }
    public static CompanyFeatureSnapshot Parse(SdkElement preferences,string version,int? statusCode=0)
    {
        if(preferences.Name!="PreferencesRet")throw new QbException("invalid_response","Preference discovery received another record type.");
        (string? Value,string Reason) Field(string group,string field)
        {
            var groups=preferences.Children.Where(c=>c.Name==group).ToArray();
            if(groups.Length==0)return(null,"not_returned");
            if(groups.Length!=1)return(null,"duplicate_path");
            var values=groups[0].Children.Where(c=>c.Name==field).ToArray();
            if(values.Length==0)return(null,"not_returned");
            if(values.Length!=1)return(null,"duplicate_path");
            if(values[0].Children.Count!=0 || values[0].Attributes.Count!=0)return(null,"invalid_value");
            return(values[0].Value,"observed");
        }
        var facts=Flags.Select(flag=>
        {
            var (value,reason)=Field(flag.Group,flag.Field);
            bool? enabled=value switch {"true" or "1"=>true,"false" or "0"=>false,_=>null};
            return new CompanyFeatureFact(flag.Id,"PreferencesRet/"+flag.Group+"/"+flag.Field,enabled,
                enabled is null ? null : enabled.Value ? "true" : "false",enabled is null ? "unknown" : enabled.Value ? "enabled" : "disabled",
                enabled is not null ? "observed" : reason=="observed" ? "invalid_value" : reason);
        }).ToList();
        var (serialValue,serialReason)=Field("ItemsAndInventoryPreferences","IsTrackingSerialOrLotNumber");
        bool? serialEnabled=serialValue switch {"None"=>false,"SerialNumber" or "LotNumber"=>true,_=>null};
        facts.Add(new("serial_or_lot_tracking",SerialPath,serialEnabled,serialEnabled is null ? null : serialValue,
            serialEnabled is null ? "unknown" : serialEnabled.Value ? "enabled" : "disabled",
            serialEnabled is not null ? "observed" : serialReason=="observed" ? "invalid_value" : serialReason));
        return new("succeeded","PreferencesQuery",version,statusCode,facts);
    }
}
