using System.Text.Json;
using QbCli.Core;

namespace QbCli;

public static class Output
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static string Json(object result) => JsonSerializer.Serialize(result, result.GetType(), JsonOptions);
    public static string Safe(string? value) => new((value ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    public static void Write(TextWriter writer, object result, bool json)
    {
        if (json) { writer.WriteLine(Json(result)); return; }
        switch (result)
        {
            case TransactionsResult history:
                writer.WriteLine($"Company: {Safe(history.Company)}");
                writer.WriteLine("ID  TYPE  DATE  NUMBER  ENTITY  ACCOUNT  AMOUNT  CURRENCY  MEMO");
                foreach (var transaction in history.Results)
                {
                    var currency = string.IsNullOrEmpty(transaction.Currency?.FullName) ? "Unknown" : Safe(transaction.Currency.FullName);
                    writer.WriteLine($"{Safe(transaction.Id)}  {Safe(transaction.Type)}  {Safe(transaction.Date)}  {Safe(transaction.Number)}  {Safe(transaction.Entity?.FullName)}  {Safe(transaction.Account?.FullName)}  {transaction.Amount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Unknown"}  {currency}  {Safe(transaction.Memo)}");
                }
                writer.WriteLine($"{history.Results.Count} results from {history.ScannedRecords} summary records; complete: {history.Complete}.");
                writer.WriteLine(Safe(history.CoverageScope)); break;
            case FinancialReportResult report:
                writer.WriteLine($"Company: {Safe(report.Company)}");
                writer.WriteLine($"{Safe(report.Title)} — {Safe(report.Subtitle)}; basis: {Safe(report.Basis)}");
                writer.WriteLine(string.Join("  |  ", report.Columns.Select(c => string.Join(" / ", c.Titles.Select(t => Safe(t.Text))))));
                foreach (var row in report.Rows)
                {
                    var values = row.Cells.ToDictionary(c => c.ColumnId);
                    writer.WriteLine(row.Kind == "text" ? Safe(row.Text) : string.Join("  |  ", report.Columns.Select(c => values.TryGetValue(c.Id, out var cell) ? Safe(cell.Text) : "")));
                }
                writer.WriteLine($"{report.Rows.Count} rows; complete: {report.Complete}."); break;
            case DocumentsResult d:
                writer.WriteLine($"Company: {Safe(d.Company)}");
                writer.WriteLine("ID  NUMBER  DATE  DUE DATE  PARTY  REMAINING  CURRENCY  PAID");
                foreach (var document in d.Results)
                {
                    var currency = string.IsNullOrEmpty(document.Currency?.Name) ? "Unknown" : Safe(document.Currency.Name);
                    writer.WriteLine($"{Safe(document.Id)}  {Safe(document.Number)}  {Safe(document.Date)}  {Safe(document.DueDate)}  {Safe(document.Party.FullName)}  {document.RemainingAmount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Unknown"}  {currency}  {document.Paid?.ToString() ?? "Unknown"}");
                }
                writer.WriteLine($"{d.Results.Count} {d.DocumentType} records; open only: {d.OpenOnly}; complete: {d.Complete}."); break;
            case PartiesResult p:
                writer.WriteLine($"Company: {Safe(p.Company)}");
                writer.WriteLine("ID  FULL NAME  COMPANY  ACTIVE");
                foreach (var party in p.Results) writer.WriteLine($"{Safe(party.Id)}  {Safe(party.FullName)}  {Safe(party.CompanyName)}  {party.Active?.ToString() ?? "Unknown"}");
                writer.WriteLine($"{p.Results.Count} {p.EntityType} records; complete: {p.Complete}."); break;
            case CompanyResult c:
                writer.WriteLine($"Company: {Safe(c.Info.Name)}");
                writer.WriteLine($"Legal name: {Safe(c.Info.LegalName)}"); break;
            case AccountsResult a:
                writer.WriteLine($"Company: {Safe(a.Company)}");
                var rows = a.Accounts.Select(x => new[] { Safe(x.Id), Safe(x.FullName), Safe(x.Type), x.Active switch { true => "Yes", false => "No", null => "Unknown" } }).ToList();
                rows.Insert(0, ["ID", "ACCOUNT", "TYPE", "ACTIVE"]);
                int[] widths = Enumerable.Range(0, 4).Select(i => rows.Max(row => row[i].Length)).ToArray();
                foreach (var row in rows) writer.WriteLine(string.Join("  ", row.Select((cell, i) => cell.PadRight(widths[i]))).TrimEnd());
                writer.WriteLine($"{a.Accounts.Count} accounts (including inactive)."); break;
            case ErrorResult e: writer.WriteLine($"Error [{Safe(e.Error.Code)}]: {Safe(e.Error.Message)}"); break;
            default: writer.WriteLine(Json(result)); break;
        }
    }
}

