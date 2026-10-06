using Microsoft.Win32;
using System.Runtime.Versioning;
using QbCli.Core;

namespace QbCli.QuickBooks;

/// <summary>Owns a fresh per-user CLSID key for a currently running callback only.</summary>
[SupportedOSPlatform("windows")]
public sealed class TemporaryCallbackRegistration : IDisposable
{
    private readonly string path;
    private readonly string ownership=Guid.NewGuid().ToString("N");
    public TemporaryCallbackRegistration(Guid clsid,string executable,string companyFile)
    {
        if(!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new QbException("invalid_arguments","Callback registration requires the existing executable path.");
        using var existing=Registry.ClassesRoot.OpenSubKey("CLSID\\"+clsid.ToString("B"));
        if(existing is not null) throw new QbException("callback_identity_in_use","Callback CLSID is already registered. Use a new callback identity.");
        path="Software\\Classes\\CLSID\\"+clsid.ToString("B");
        // The per-CLSID listener mutex excludes other qbcli registrations.
        using var key=Registry.CurrentUser.CreateSubKey(path,true);
        try
        {
            key.SetValue("","McMurry Software qbcli temporary event callback");key.SetValue("QbCliOwner",ownership);
            key.SetValue("QbCliCompanyFile",Path.GetFullPath(companyFile));
            using var server=key.CreateSubKey("LocalServer32",true);
            server.SetValue("","\""+executable+"\"");
            key.Flush();server.Flush();
        }
        catch {key.Close();Dispose();throw;}
    }
    public void Dispose()
    {
        using(var key=Registry.CurrentUser.OpenSubKey(path))
            if(key is null || key.GetValue("QbCliOwner") as string != ownership) return;
        // Remove only this fresh key while its ownership marker still matches.
        Registry.CurrentUser.DeleteSubKeyTree(path,false);
    }
    public static object Cleanup(Guid clsid,string companyFile,bool apply)
    {
        if(clsid==Guid.Empty) throw new QbException("invalid_arguments","Cleanup requires a nonempty callback identity.");
        string path="Software\\Classes\\CLSID\\"+clsid.ToString("B");
        using var gate=new Mutex(false,"Local\\McMurrySoftware.qbcli.Callback."+clsid.ToString("N"));
        bool owned;try {owned=gate.WaitOne(0);} catch(AbandonedMutexException) {owned=true;}
        if(!owned) throw new QbException("callback_identity_in_use","Stop the live listener normally before cleaning its registration.");
        try
        {
            bool exists;
            using(var key=Registry.CurrentUser.OpenSubKey(path))
            {
                exists=key is not null;
                if(key is not null && (key.GetValue("") as string is not ("McMurry Software qbcli temporary event callback" or PersistentCallbackRegistration.Description) || !Guid.TryParseExact(key.GetValue("QbCliOwner") as string,"N",out _)
                    || key.GetValue("QbCliCompanyFile") is not string recordedCompany || !string.Equals(Path.GetFullPath(recordedCompany),Path.GetFullPath(companyFile),StringComparison.OrdinalIgnoreCase)))
                    throw new QbException("callback_registration_not_owned","Cleanup refuses a registration without qbcli ownership and matching company scope.");
            }
            if(exists && apply) Registry.CurrentUser.DeleteSubKeyTree(path,false);
            return new {success=true,schemaVersion=1,mode=apply?"apply":"preview",callbackClsid=clsid.ToString("B"),companyFile,registrationExists=exists,removed=exists&&apply,
                guidance="Only a stopped, owned current-user callback registration is removed. SDK subscriptions are separate; query/delete only your known SubscriberID through sdk subscription."};
        }
        finally {gate.ReleaseMutex();}
    }
}
