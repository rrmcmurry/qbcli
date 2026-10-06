using System.Globalization;
using System.Text.Json.Nodes;
using QbCli.Core;

namespace QbCli.QuickBooks;

public static class TransactionQueries
{
    public const string Coverage = "TransactionQuery summaries visible to the SDK user and its permissions/features; TimeTracking is excluded by the SDK. Complete means the requested iterator finished, not universal company transaction coverage. Memo search matches summary memo only; no posting/item/group-line descriptions are searched.";
    public static SdkPlan Plan(TransactionSelection selection, string country)
    {
        selection.Validate();
        var fields = new JsonObject { ["TransactionDetailLevelFilter"] = "SummaryOnly" };
        if (selection.Since is not null || selection.Until is not null)
        {
            var dates = new JsonObject();
            if (selection.Since is not null) dates["FromTxnDate"] = selection.Since;
            if (selection.Until is not null) dates["ToTxnDate"] = selection.Until;
            fields["TransactionDateRangeFilter"] = dates;
        }
        // There is no documented Memo filter on TransactionQuery. Filtering
        // summary memos locally requires retrieving every selected page first.
        return new("13.0", country, "stopOnError", [new("history", "TransactionQuery", fields)]);
    }
    public static TransactionSummary Normalize(SdkElement record)
    {
        if (record.Name != "TransactionRet") throw Invalid();
        SdkElement? Single(SdkElement node, string name)
        {
            var matches = node.Children.Where(c => c.Name == name).ToArray();
            if (matches.Length > 1) throw Invalid();
            return matches.SingleOrDefault();
        }
        string? Scalar(SdkElement node, string name, bool required = false)
        {
            var field = Single(node, name);
            if (field is not null && field.Children.Count != 0 || required && string.IsNullOrWhiteSpace(field?.Value)) throw Invalid();
            return field?.Value;
        }
        TransactionReference? Reference(string name)
        {
            var field = Single(record, name);
            if (field is null) return null;
            if (field.Value is not null) throw Invalid();
            return new(Scalar(field, "ListID"), Scalar(field, "FullName"));
        }
        // SummaryOnly must not be quietly combined with posting-line data.
        if (Single(record, "TxnLineID") is not null) throw Invalid();
        string date = Scalar(record, "TxnDate", true)!;
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw Invalid();
        decimal? amount = null;
        if (Scalar(record, "Amount") is { } text)
        {
            if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)) throw Invalid();
            amount = parsed;
        }
        return new(Scalar(record, "TxnID", true)!, Scalar(record, "TxnType", true)!, date, Scalar(record, "RefNumber"), amount,
            Scalar(record, "Memo"), Reference("EntityRef"), Reference("AccountRef"), Reference("CurrencyRef"));
    }
    public static IReadOnlyList<TransactionSummary> Select(IEnumerable<TransactionSummary> records, TransactionSelection selection)
    {
        selection.Validate();
        return records.Where(r => selection.MemoContains is null || r.Memo?.Contains(selection.MemoContains, StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(r => r.Date, StringComparer.Ordinal).ThenBy(r => r.Type, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
    }
    private static QbException Invalid() => new("invalid_response", "The SDK transaction summary has missing, duplicate, malformed or unexpected posting-line fields.");
}
