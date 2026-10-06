using System.Globalization;

namespace QbCli.Core;

public sealed record TransactionSelection(string? Since = null, string? Until = null, string? MemoContains = null)
{
    public void Validate()
    {
        bool Date(string? text) => text is null || DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        if (!Date(Since) || !Date(Until) || Since is not null && Until is not null && string.CompareOrdinal(Since, Until) > 0 || MemoContains is not null && string.IsNullOrWhiteSpace(MemoContains))
            throw new QbException("invalid_arguments", "Transaction dates require yyyy-MM-dd in order; memo search requires nonempty text.");
    }
}
public sealed record TransactionReference(string? Id, string? FullName);
public sealed record TransactionSummary(string Id, string Type, string Date, string? Number, decimal? Amount,
    string? Memo, TransactionReference? Entity, TransactionReference? Account, TransactionReference? Currency);
public sealed record TransactionsResult(bool Success, int SchemaVersion, string? Company, string CompanyFile,
    TransactionSelection Selection, bool Complete, int Pages, int ScannedRecords, string CoverageScope, IReadOnlyList<TransactionSummary> Results);
public interface ITransactionReader
{
    TransactionsResult ReadTransactions(TransactionSelection selection, int pageSize, string expectedCompanyFile);
}
