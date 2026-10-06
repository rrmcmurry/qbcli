namespace QbCli.Core;

public sealed record CompanyInfo(string? Name, string? LegalName);
public sealed record Account(string Id, string Name, string FullName, string Type, bool? Active);
public sealed record CompanyResult(bool Success, int SchemaVersion, string? Company, CompanyInfo Info);
public sealed record AccountsResult(bool Success, int SchemaVersion, string? Company, IReadOnlyList<Account> Accounts);
public sealed record ErrorInfo(string Code, string Message, string? DiagnosticCode = null);
public sealed record ErrorResult(bool Success, int SchemaVersion, ErrorInfo Error);

public sealed class QbException(string code, string message, string? diagnosticCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
    public string? DiagnosticCode { get; } = diagnosticCode;
}

// Domain contract contains no XML, COM objects, or vendor-specific session tickets.
public interface IBooksReader : IDisposable
{
    CompanyInfo GetCompany();
    IReadOnlyList<Account> GetAccounts();
    ConnectionInfo GetConnectionInfo();
    IReadOnlyList<Party> GetParties(PartyKind kind, PartySelection selection, int pageSize);
}

public sealed class ReadService(IBooksReader reader)
{
    public CompanyResult Company() => BuildCompany(reader.GetCompany());
    public AccountsResult Accounts()
    {
        var company = reader.GetCompany();
        var accounts = reader.GetAccounts().OrderBy(a => a.FullName, StringComparer.Ordinal)
            .ThenBy(a => a.Id, StringComparer.Ordinal).ToArray();
        return new(true, 1, company.Name, accounts);
    }
    private static CompanyResult BuildCompany(CompanyInfo info) => new(true, 1, info.Name, info);
}


