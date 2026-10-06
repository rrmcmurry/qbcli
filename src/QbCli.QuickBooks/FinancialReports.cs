using System.Globalization;
using System.Text.Json.Nodes;
using QbCli.Core;

namespace QbCli.QuickBooks;

public static class FinancialReports
{
    public static SdkPlan Plan(ReportSelection selection, string country)
    {
        selection.Validate();
        string type = selection.Kind switch
        {
            FinancialReportKind.TrialBalance => "TrialBalance", FinancialReportKind.ProfitLoss => "ProfitAndLossStandard",
            FinancialReportKind.BalanceSheet => "BalanceSheetStandard", _ => throw Invalid()
        };
        var period = new JsonObject { ["ToReportDate"] = selection.To ?? selection.AsOf };
        if (selection.From is not null) period["FromReportDate"] = selection.From;
        return new("13.0", country, "stopOnError", [new("report", "GeneralSummaryReportQuery", new()
        {
            ["GeneralSummaryReportType"] = type, ["DisplayReport"] = false, ["ReportPeriod"] = period,
            ["ReportBasis"] = selection.Basis == "cash" ? "Cash" : "Accrual", ["ReturnRows"] = "All", ["ReturnColumns"] = "All"
        })]);
    }
    public static FinancialReportResult Normalize(SdkElement report, ReportSelection selection, string? company, string companyFile)
    {
        selection.Validate();
        if (report.Name != "ReportRet") throw Invalid();
        var allowedRoot = new HashSet<string> { "ReportTitle", "ReportSubtitle", "ReportBasis", "NumRows", "NumColumns", "NumColTitleRows", "ColDesc", "ReportData" };
        if (report.Children.Any(c => !allowedRoot.Contains(c.Name))) throw Invalid();
        int rows = Count(report, "NumRows"), columns = Count(report, "NumColumns"), titleRows = Count(report, "NumColTitleRows");
        if (rows > 1_000_000 || columns > 1000 || titleRows > 1000) throw new QbException("result_limit", "The report exceeds supported dimensions.");
        var normalizedColumns = new List<ReportColumn>();
        var columnIds = new HashSet<int>();
        foreach (var col in report.Children.Where(c => c.Name == "ColDesc"))
        {
            if (col.Children.Where(c => c.Name == "ColTitle").Any(c => c.Children.Count != 0)) throw Invalid();
            int id = Positive(Attribute(col, "colID", true));
            if (!columnIds.Add(id) || col.Children.Any(c => c.Name is not ("ColTitle" or "ColType"))) throw Invalid();
            string valueType = ValueType(Attribute(col, "dataType", true)!);
            var titles = col.Children.Where(c => c.Name == "ColTitle").Select(c => new ReportTitleLine(Positive(Attribute(c, "titleRow", true)), Attribute(c, "value"))).ToArray();
            if (titles.Any(t => t.Row > titleRows) || titles.Select(t => t.Row).Distinct().Count() != titles.Length) throw Invalid();
            normalizedColumns.Add(new(id, valueType, Scalar(col, "ColType"), titles));
        }
        if (normalizedColumns.Count != columns) throw Invalid();
        var data = Single(report, "ReportData", rows > 0);
        var normalizedRows = new List<ReportRow>();
        var rowNumbers = new HashSet<int>();
        var columnsById = normalizedColumns.ToDictionary(c => c.Id);
        foreach (var row in data?.Children ?? [])
        {
            string kind = row.Name switch { "TextRow" => "text", "DataRow" => "data", "SubtotalRow" => "subtotal", "TotalRow" => "total", _ => throw Invalid() };
            int number = Positive(Attribute(row, "rowNumber", true));
            if (!rowNumbers.Add(number) || row.Children.Any(c => c.Name is not ("RowData" or "ColData")) || kind == "text" && row.Children.Count != 0) throw Invalid();
            var entity = Single(row, "RowData");
            if (entity is not null && entity.Children.Count != 0) throw Invalid();
            var cells = new List<ReportCell>();
            var cellIds = new HashSet<int>();
            foreach (var cell in row.Children.Where(c => c.Name == "ColData"))
            {
                if (cell.Children.Count != 0) throw Invalid();
                int id = Positive(Attribute(cell, "colID", true));
                if (!cellIds.Add(id) || !columnsById.TryGetValue(id, out var column)) throw Invalid();
                string? text = Attribute(cell, "value");
                string valueType = Attribute(cell, "dataType") is { } cellType ? ValueType(cellType) : column.ValueType;
                decimal? amount = null;
                // Preserve absent, blank and explicit zero separately. Do not
                // sum subtotal/total rows or infer currency/account identities.
                if (valueType == "amount" && !string.IsNullOrEmpty(text))
                {
                    if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)) throw Invalid();
                    amount = parsed;
                }
                cells.Add(new(id, valueType, text, amount));
            }
            normalizedRows.Add(new(number, kind, Attribute(row, "value"), entity is null ? null : Attribute(entity, "rowType"),
                entity is null ? null : Attribute(entity, "value"), cells));
        }
        if (normalizedRows.Count != rows) throw Invalid();
        var basis = Scalar(report, "ReportBasis");
        if (basis is not (null or "Cash" or "Accrual" or "None")) throw Invalid();
        return new(true, 1, company, companyFile, selection, Scalar(report, "ReportTitle"), Scalar(report, "ReportSubtitle"),
            basis?.ToLowerInvariant(), true, titleRows, normalizedColumns, normalizedRows);
    }
    private static SdkElement? Single(SdkElement node, string name, bool required = false)
    {
        var matches = node.Children.Where(c => c.Name == name).ToArray();
        if (matches.Length > 1 || required && matches.Length == 0) throw Invalid();
        return matches.SingleOrDefault();
    }
    private static string? Scalar(SdkElement node, string name)
    {
        var field = Single(node, name);
        if (field is not null && field.Children.Count != 0) throw Invalid();
        return field?.Value;
    }
    private static string? Attribute(SdkElement node, string name, bool required = false)
    {
        var value = node.Attributes.GetValueOrDefault(name);
        if (required && string.IsNullOrWhiteSpace(value)) throw Invalid();
        return value;
    }
    private static int Count(SdkElement node, string name)
    {
        if (!int.TryParse(Scalar(node, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0) throw Invalid();
        return value;
    }
    private static int Positive(string? text)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0) throw Invalid();
        return value;
    }
    private static string ValueType(string type) => type switch
    {
        "AMTTYPE" => "amount", "STRTYPE" => "text", "DATETYPE" => "date", "DATETIMETYPE" => "dateTime",
        "INTTYPE" => "integer", "QUANTYPE" => "quantity", "PERCENTTYPE" => "percent", "BOOLTYPE" => "boolean",
        _ => throw Invalid()
    };
    private static QbException Invalid() => new("invalid_response", "The SDK report contains missing, duplicate, inconsistent or unsupported report fields.");
}
