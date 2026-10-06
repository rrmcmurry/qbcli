// Offline comparison of new normalization against retained live SDK reports.
// No COM activation or connection is performed. Only metadata is exported.
using System.Globalization;
using System.Text.Json;
using QbCli.Core;
using QbCli.QuickBooks;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Supply the absolute project root.");
var root = args[0];
var fixture = Path.Combine(root, "artifacts", "fixtures", "lifecycle-165127-report-variants-result.json");
var planPath = fixture.Replace("-result.json", "-plan.json");
using var source = JsonDocument.Parse(File.ReadAllText(fixture));
using var plan = JsonDocument.Parse(File.ReadAllText(planPath));
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var checks = new List<object>();
var types = new Dictionary<string, FinancialReportKind> { ["TrialBalance"] = FinancialReportKind.TrialBalance, ["ProfitAndLossStandard"] = FinancialReportKind.ProfitLoss, ["BalanceSheetStandard"] = FinancialReportKind.BalanceSheet };
foreach (var request in plan.RootElement.GetProperty("requests").EnumerateArray())
{
    var fields = request.GetProperty("fields");
    if (!fields.TryGetProperty("GeneralSummaryReportType", out var reportType) || !types.TryGetValue(reportType.GetString()!, out var kind)) continue;
    var result = source.RootElement.GetProperty("results").EnumerateArray().Single(r => r.GetProperty("requestId").GetString() == request.GetProperty("requestId").GetString());
    if (result.GetProperty("outcome").GetString() != "succeeded") throw new Exception("Retained report was not successful.");
    var raw = result.GetProperty("data")[0].Deserialize<SdkElement>(options)!;
    var period = fields.GetProperty("ReportPeriod");
    var from = period.GetProperty("FromReportDate").GetString(); var to = period.GetProperty("ToReportDate").GetString();
    var selection = kind == FinancialReportKind.ProfitLoss ? new ReportSelection(kind, from, to, null) : new(kind, null, null, to);
    var normalized = FinancialReports.Normalize(raw, selection, null, source.RootElement.GetProperty("companyFile").GetString()!);
    var rows = raw.Children.Single(n => n.Name == "ReportData").Children;
    var rawColumns = raw.Children.Where(n => n.Name == "ColDesc").ToArray();
    if (rawColumns.Length != normalized.Columns.Count) throw new Exception("Column count changed.");
    for (int i = 0; i < rawColumns.Length; i++)
    {
        var original = rawColumns[i]; var actual = normalized.Columns[i];
        if (actual.Id.ToString(CultureInfo.InvariantCulture) != original.Attributes["colID"] || actual.Meaning != original.Children.Single(n => n.Name == "ColType").Value) throw new Exception("Column identity/meaning changed.");
        var titles = original.Children.Where(n => n.Name == "ColTitle").ToArray();
        if (titles.Length != actual.Titles.Count) throw new Exception("Title count changed.");
        for (int j = 0; j < titles.Length; j++)
            if (actual.Titles[j].Row.ToString(CultureInfo.InvariantCulture) != titles[j].Attributes["titleRow"] || actual.Titles[j].Text != titles[j].Attributes.GetValueOrDefault("value")) throw new Exception("Column title changed.");
    }
    if (normalized.Title != raw.Children.Single(n => n.Name == "ReportTitle").Value || normalized.Subtitle != raw.Children.Single(n => n.Name == "ReportSubtitle").Value) throw new Exception("Report title changed.");
    if (rows.Count != normalized.Rows.Count) throw new Exception("Row count changed.");
    int cells = 0, sparseRows = 0, headings = 0;
    for (int i = 0; i < rows.Count; i++)
    {
        var original = rows[i]; var actual = normalized.Rows[i];
        if (actual.Number.ToString(CultureInfo.InvariantCulture) != original.Attributes["rowNumber"]) throw new Exception("Row number changed.");
        var expectedKind = original.Name switch { "DataRow" => "data", "TextRow" => "text", "SubtotalRow" => "subtotal", "TotalRow" => "total", _ => throw new Exception("Unknown row.") };
        if (actual.Kind != expectedKind || actual.Text != original.Attributes.GetValueOrDefault("value")) throw new Exception("Row kind/text changed.");
        if (actual.Kind == "text") headings++;
        var entity = original.Children.SingleOrDefault(n => n.Name == "RowData");
        if (actual.EntityType != entity?.Attributes.GetValueOrDefault("rowType") || actual.EntityName != entity?.Attributes.GetValueOrDefault("value")) throw new Exception("Row entity changed.");
        var originalCells = original.Children.Where(c => c.Name == "ColData").ToArray();
        if (originalCells.Length != actual.Cells.Count) throw new Exception("Cell count changed.");
        if (actual.Kind != "text" && originalCells.Length < normalized.Columns.Count) sparseRows++;
        for (int j = 0; j < originalCells.Length; j++)
        {
            var originalCell = originalCells[j]; var cell = actual.Cells[j];
            if (cell.ColumnId.ToString(CultureInfo.InvariantCulture) != originalCell.Attributes["colID"] || cell.Text != originalCell.Attributes.GetValueOrDefault("value")) throw new Exception("Cell identity/value changed.");
            if (cell.ValueType == "amount" && !string.IsNullOrEmpty(cell.Text) && cell.Amount != decimal.Parse(cell.Text, CultureInfo.InvariantCulture)) throw new Exception("Amount changed.");
            cells++;
        }
    }
    var output = fixture.Replace("-result.json", "-" + kind.ToString().ToLowerInvariant() + "-normalized.json");
    File.WriteAllText(output, JsonSerializer.Serialize(normalized, options));
    checks.Add(new { reportType = reportType.GetString(), rows = rows.Count, columns = normalized.Columns.Count, cells, sparseRows, headings,
        sourceRequestSucceeded = true, everyColumnTitleRowKindNumberEntityAndCellValuePreserved = true, normalizedComplete = normalized.Complete,
        scope = "Offline normalization of retained successful live report data; not a fresh request through the new convenience handler, not verification of its All rows/columns or date selection." });
}
if (checks.Count != 3) throw new Exception("Missing one of the three report fixtures.");
File.WriteAllText(fixture.Replace("-result.json", "-report-normalization-verification.json"), JsonSerializer.Serialize(checks, options));
Console.WriteLine(JsonSerializer.Serialize(checks, options));
