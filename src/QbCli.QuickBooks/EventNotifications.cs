using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using QbCli.Core;

namespace QbCli.QuickBooks;

public static class EventNotifications
{
    public static IReadOnlyList<EventNotification> Parse(string xml)
    {
        try
        {
            using var reader=XmlReader.Create(new StringReader(xml),new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=4*1024*1024 });
            var root=XDocument.Load(reader).Root;
            if(root?.Name != "QBXML" || root.Elements().Count()!=1 || root.Elements().Single().Name != "QBXMLEvents") throw Invalid();
            var container=root.Elements().Single();
            if(!container.Elements().Any()) throw Invalid();
            var result=new List<EventNotification>();
            foreach(var element in container.Elements())
            {
                if(element.Name.LocalName is not ("DataEvent" or "UIEvent" or "UIExtensionEvent") || element.Name.NamespaceName.Length>0) throw Invalid();
                var paths=element.Elements("CompanyFilePath").ToArray();
                if(paths.Length!=1 || paths[0].HasElements || string.IsNullOrWhiteSpace(paths[0].Value) || !Path.IsPathFullyQualified(paths[0].Value)) throw Invalid();
                result.Add(new(element.Name.LocalName,paths[0].Value,Element(element)));
            }
            return result;
        }
        catch(XmlException) {throw Invalid();}
    }
    private static SdkElement Element(XElement node,int depth=0)
    {
        if(depth>64 || node.Name.NamespaceName.Length>0 || node.Attributes().Any(a=>a.Name.NamespaceName.Length>0)) throw Invalid();
        return new(node.Name.LocalName,node.Attributes().ToDictionary(a=>a.Name.LocalName,a=>a.Value,StringComparer.Ordinal),
            node.HasElements?null:node.Value,node.Elements().Select(c=>Element(c,depth+1)).ToArray());
    }
    private static QbException Invalid()=>new("invalid_event","Callback payload was not a bounded QBXMLEvents notification with explicit company identity.");
}

/// <summary>Durably captures notifications only. Never opens a QuickBooks session or writes accounting data.</summary>
public sealed class EventJournal : IDisposable
{
    private readonly FileStream stream;
    private readonly string company;
    public int Received {get;private set;}
    public int Ignored {get;private set;}
    public int Failed {get;private set;}
    public EventJournal(string path,string companyFile,string callbackClsid)
    {
        company=Path.GetFullPath(companyFile);
        stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough);
        try {Record(new {kind="listener_starting",schemaVersion=1,processId=Environment.ProcessId,callbackClsid,companyFile=company,callbackInterface="A3A4E33E-E747-48CF-947B-93FDE67FF72F"});}
        catch {stream.Dispose();throw;}
    }
    public void Ready()=>Record(new {kind="listener_ready"});
    public int Receive(string xml)
    {
        try
        {
            // Parse the entire payload before persisting any part of it.
            var notifications=EventNotifications.Parse(xml);
            foreach(var notification in notifications)
            {
                if(!string.Equals(Path.GetFullPath(notification.CompanyFile),company,StringComparison.OrdinalIgnoreCase)) {Ignored++;continue;}
                Record(new {kind="event",receivedAt=DateTimeOffset.UtcNow,notification});Received++;
            }
            return 0;
        }
        catch(Exception ex) when(ex is not OutOfMemoryException && ex is not StackOverflowException)
        {Failed++;return unchecked((int)0x80004005);}
    }
    public void Complete()=>Record(new {kind="listener_finished",received=Received,ignored=Ignored,failed=Failed});
    private void Record(object value)
    {
        var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,new JsonSerializerOptions {PropertyNamingPolicy=JsonNamingPolicy.CamelCase})+"\n");
        stream.Write(bytes);stream.Flush(true);
    }
    public void Dispose()=>stream.Dispose();
}
