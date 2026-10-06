using System.Globalization;
using System.Text.Json.Serialization;

namespace QbCli.Core;

[JsonConverter(typeof(JsonStringEnumConverter<FinancialReportKind>))]
public enum FinancialReportKind { TrialBalance, ProfitLoss, BalanceSheet }
public sealed record ReportSelection(FinancialReportKind Kind, string? From, string? To, string? AsOf, string Basis = "accrual")
{
    public void Validate()
    {
        bool Date(string? value) => value is not null && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        if (Basis is not ("accrual" or "cash") || !Enum.IsDefined(Kind)) throw Invalid();
        if (Kind == FinancialReportKind.ProfitLoss)
        {
            if (!Date(From) || !Date(To) || AsOf is not null || string.CompareOrdinal(From, To) > 0) throw Invalid();
        }
        else if (!Date(AsOf) || From is not null || To is not null) throw Invalid();
    }
    private static QbException Invalid() => new("invalid_arguments", "Profit-loss requires --from and --to in yyyy-MM-dd order; trial-balance/balance-sheet require --as-of. --basis must be accrual or cash.");
}
public sealed record ReportTitleLine(int Row, string? Text);
public sealed record ReportColumn(int Id, string ValueType, string? Meaning, IReadOnlyList<ReportTitleLine> Titles);
public sealed record ReportCell(int ColumnId, string ValueType, string? Text, decimal? Amount);
public sealed record ReportRow(int Number, string Kind, string? Text, string? EntityType, string? EntityName, IReadOnlyList<ReportCell> Cells);
public sealed record FinancialReportResult(bool Success, int SchemaVersion, string? Company, string CompanyFile,
    ReportSelection Selection, string? Title, string? Subtitle, string? Basis, bool Complete,
    int TitleRowCount, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<ReportRow> Rows);
public interface IFinancialReportReader
{
    FinancialReportResult ReadReport(ReportSelection selection, string expectedCompanyFile);
}
