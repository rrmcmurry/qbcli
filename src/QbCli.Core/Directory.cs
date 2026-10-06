namespace QbCli.Core;

public enum PartyKind { Customer, Vendor }
public enum PartySelectionKind { All, Contains, Id, FullName }
public sealed record PartySelection(PartySelectionKind Kind = PartySelectionKind.All, string? Value = null);
public sealed record Party(string Id, string Name, string FullName, bool? Active, string? CompanyName);
public sealed record PartiesResult(bool Success, int SchemaVersion, string? Company, string EntityType, bool Complete, IReadOnlyList<Party> Results);
public sealed record HostInfo(string ProductName, string MajorVersion, string MinorVersion, string? Country,
    IReadOnlyList<string> SupportedProtocolVersions, bool? AutomaticLogin, string? FileMode);
public sealed record ConnectionInfo(CompanyInfo Company, HostInfo Host, string CompanyFile, string QueryProtocolVersion, string? RequestedFileMode = null);

public static class PartyService
{
    public static PartiesResult Read(IBooksReader reader, PartyKind kind, PartySelection selection, int pageSize)
    {
        var company = reader.GetCompany();
        var records = reader.GetParties(kind, selection, pageSize);
        var ordered = records.OrderBy(p => p.FullName, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();
        if (selection.Kind is PartySelectionKind.Id or PartySelectionKind.FullName)
        {
            if (ordered.Length == 0) throw new QbException("not_found", "No matching customer or vendor was found.");
            if (ordered.Length != 1) throw new QbException("ambiguous_match", "The lookup matched multiple records; use an explicit ID.");
        }
        return new(true, 1, company.Name, kind.ToString().ToLowerInvariant(), true, ordered);
    }
}

