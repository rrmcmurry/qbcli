using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using QbCli.Core;

namespace QbCli.QuickBooks;

public sealed record PartyPage(IReadOnlyList<Party> Records, string? IteratorId, int Remaining);

public static class QbXml
{
    public const string ProtocolVersion = "6.0";
    public static string CompanyRequest() => Wrap(CompanyQuery());
    private static XElement CompanyQuery() => new("CompanyQueryRq", Fields("CompanyName", "LegalCompanyName"));
    public static string AccountsRequest() => Wrap(new XElement("AccountQueryRq", new XElement("ActiveStatus", "All"),
        Fields("ListID", "Name", "FullName", "AccountType", "IsActive")));
    public static string ConnectionRequest()
    {
        var company = CompanyQuery(); company.SetAttributeValue("requestID", "company");
        return Wrap(new XElement("HostQueryRq", new XAttribute("requestID", "host")), company);
    }
    private static IEnumerable<XElement> Fields(params string[] names) => names.Select(name => new XElement("IncludeRetElement", name));
    private static string Wrap(params XElement[] queries) => "<?xml version=\"1.0\"?><?qbxml version=\"" + ProtocolVersion + "\"?>" +
        new XElement("QBXML", new XElement("QBXMLMsgsRq", new XAttribute("onError", "stopOnError"), queries))
            .ToString(SaveOptions.DisableFormatting);
    public static string PartiesRequest(PartyKind kind, PartySelection selection, int pageSize, string? iteratorId = null)
    {
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var rq = new XElement(kind + "QueryRq");
        if (selection.Kind is PartySelectionKind.Id or PartySelectionKind.FullName)
        {
            if (string.IsNullOrWhiteSpace(selection.Value)) throw new ArgumentException("Lookup requires a value.", nameof(selection));
            rq.Add(new XElement(selection.Kind == PartySelectionKind.Id ? "ListID" : "FullName", selection.Value));
        }
        else
        {
            rq.SetAttributeValue("iterator", iteratorId is null ? "Start" : "Continue");
            if (iteratorId is not null) rq.SetAttributeValue("iteratorID", iteratorId);
            rq.Add(new XElement("MaxReturned", pageSize), new XElement("ActiveStatus", "All"));
        }
        rq.Add(Fields("ListID", "Name", "FullName", "IsActive", "CompanyName"));
        return Wrap(rq);
    }
    public static CompanyInfo Company(string xml) => ReadCompany(Response(xml, "CompanyQueryRs"));
    private static CompanyInfo ReadCompany(XElement rs)
    {
        var ret = rs.Element("CompanyRet") ?? throw Invalid();
        return new((string?)ret.Element("CompanyName"), (string?)ret.Element("LegalCompanyName"));
    }
    public static IReadOnlyList<Account> Accounts(string xml) => Response(xml, "AccountQueryRs").Elements("AccountRet")
        .Select(ret => new Account(Required(ret, "ListID"), Required(ret, "Name"), Required(ret, "FullName"),
            Required(ret, "AccountType"), Boolean(ret, "IsActive"))).ToArray();
    public static (CompanyInfo Company, HostInfo Host) Connection(string xml)
    {
        var responses = Envelope(xml);
        if (responses.Length != 2) throw Invalid();
        var host = Correlated(responses, "host", "HostQueryRs");
        var company = Correlated(responses, "company", "CompanyQueryRs");
        var ret = host.Element("HostRet") ?? throw Invalid();
        var versions = ret.Elements("SupportedQBXMLVersion").Select(v => v.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (versions.Length == 0 || versions.Any(string.IsNullOrWhiteSpace)) throw Invalid();
        return (ReadCompany(company), new HostInfo(Required(ret, "ProductName"), Required(ret, "MajorVersion"),
            Required(ret, "MinorVersion"), (string?)ret.Element("Country"), versions, Boolean(ret, "IsAutomaticLogin"), (string?)ret.Element("QBFileMode")));
    }
    private static XElement Correlated(XElement[] responses, string id, string type)
    {
        var matches = responses.Where(rs => (string?)rs.Attribute("requestID") == id).ToArray();
        if (matches.Length != 1 || matches[0].Name != type) throw Invalid();
        Status(matches[0]); return matches[0];
    }
    public static PartyPage Parties(string xml, PartyKind kind, bool paged)
    {
        var rs = Response(xml, kind + "QueryRs", allowRemaining: paged, exactLookup: !paged);
        var records = rs.Elements(kind + "Ret").Select(ret => new Party(Required(ret, "ListID"), Required(ret, "Name"),
            Required(ret, "FullName"), Boolean(ret, "IsActive"), (string?)ret.Element("CompanyName"))).ToArray();
        int remaining = 0;
        if (paged)
        {
            var value = (string?)rs.Attribute("iteratorRemainingCount");
            if (value is null && (string?)rs.Attribute("statusCode") == "1" && records.Length == 0) remaining = 0;
            else if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out remaining) || remaining < 0) throw Invalid();
        }
        var iterator = (string?)rs.Attribute("iteratorID");
        if (remaining > 0 && (string.IsNullOrWhiteSpace(iterator) || records.Length == 0)) throw Invalid();
        return new(records, iterator, remaining);
    }
    private static XElement Response(string xml, string name, bool allowRemaining = false, bool exactLookup = false)
    {
        var responses = Envelope(xml);
        if (responses.Length != 1 || responses[0].Name != name) throw Invalid();
        var rs = responses[0];
        // An explicit ListID/FullName query for a missing entity returns 500/Warn.
        // Never normalize this warning on broad/paged queries or responses containing partial records.
        if (exactLookup && (string?)rs.Attribute("statusCode") == "500" && (string?)rs.Attribute("statusSeverity") == "Warn" && !rs.Elements().Any())
            throw new QbException("not_found", "No matching customer or vendor was found.", "500");
        Status(rs);
        if (!allowRemaining && rs.Attribute("iteratorRemainingCount") is { } remaining && remaining.Value != "0")
            throw new QbException("incomplete_response", "QuickBooks returned an incomplete query result.");
        return rs;
    }
    private static void Status(XElement rs)
    {
        if (!int.TryParse((string?)rs.Attribute("statusCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)) throw Invalid();
        bool noRecords = rs.Name.LocalName is "AccountQueryRs" or "CustomerQueryRs" or "VendorQueryRs" && code == 1 && !rs.Elements().Any();
        if ((!noRecords && code != 0) || (string?)rs.Attribute("statusSeverity") != "Info")
            throw new QbException("quickbooks_status", "QuickBooks did not return a complete successful query. Check its permissions and SDK log.", code.ToString(CultureInfo.InvariantCulture));
    }
    private static XElement[] Envelope(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 });
            var doc = XDocument.Load(reader);
            if (doc.Root?.Name != "QBXML" || doc.Root.Elements().Count() != 1 || doc.Root.Element("QBXMLMsgsRs") is not { } messages) throw Invalid();
            return messages.Elements().ToArray();
        }
        catch (XmlException ex) { throw new QbException("invalid_response", "QuickBooks returned an invalid XML response.", inner: ex); }
    }
    private static string Required(XElement ret, string name) => (string?)ret.Element(name) is { Length: > 0 } value ? value : throw Invalid();
    private static bool? Boolean(XElement ret, string name) => ((string?)ret.Element(name)) switch
    { null => null, "true" or "1" => true, "false" or "0" => false, _ => throw Invalid() };
    private static QbException Invalid() => new("invalid_response", "QuickBooks returned a response missing required data.");
}
