namespace QbCli.Core;

public enum DocumentKind { Invoice, Bill }
public sealed record DocumentParty(string Id, string? FullName);
public sealed record DocumentCurrency(string? Id, string? Name);
public sealed record DocumentSummary(string Id, string? Number, string Date, string? DueDate,
    DocumentParty Party, decimal? RemainingAmount, bool? Paid, string? Memo, DocumentCurrency? Currency);
public sealed record DocumentsResult(bool Success, int SchemaVersion, string? Company, string CompanyFile,
    string DocumentType, bool OpenOnly, bool Complete, int Pages, IReadOnlyList<DocumentSummary> Results);
public interface IDocumentReader
{
    DocumentsResult ReadDocuments(DocumentKind kind, bool openOnly, int pageSize, string expectedCompanyFile);
}
