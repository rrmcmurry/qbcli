// Offline evidence only: no COM activation, SDK session or financial mutation.
using System.Globalization;
using System.Text.Json;
using QbCli.Core;
using QbCli.QuickBooks;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Supply the absolute project root.");
var fixtures = Path.Combine(args[0], "artifacts", "fixtures");
using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "read-probes-result.json")));
var outcome = source.RootElement.GetProperty("results").EnumerateArray().Single(r => r.GetProperty("operation").GetString() == "TransactionQuery");
if (outcome.GetProperty("outcome").GetString() != "succeeded") throw new Exception("Retained query did not succeed.");
var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
var data = outcome.GetProperty("data").Deserialize<SdkElement[]>(options)!;
var normalized = data.Select(TransactionQueries.Normalize).ToArray();
for (int i = 0; i < data.Length; i++)
{
    var raw = data[i]; var actual = normalized[i];
    string? Leaf(string key) => raw.Children.SingleOrDefault(n => n.Name == key)?.Value;
    if (actual.Id != Leaf("TxnID") || actual.Type != Leaf("TxnType") || actual.Date != Leaf("TxnDate") || actual.Number != Leaf("RefNumber") || actual.Memo != Leaf("Memo")) throw new Exception("Summary field changed.");
    var text = Leaf("Amount");
    if (actual.Amount != (text is null ? (decimal?)null : decimal.Parse(text, CultureInfo.InvariantCulture))) throw new Exception("Amount changed.");
    foreach (var (field, reference) in new[] { ("EntityRef", actual.Entity), ("AccountRef", actual.Account), ("CurrencyRef", actual.Currency) })
    {
        var original = raw.Children.SingleOrDefault(c => c.Name == field);
        if ((original is null) != (reference is null)) throw new Exception("Reference presence changed.");
        if (original is not null && (reference!.Id != original.Children.SingleOrDefault(c => c.Name == "ListID")?.Value || reference.FullName != original.Children.SingleOrDefault(c => c.Name == "FullName")?.Value)) throw new Exception("Reference identity changed.");
    }
}
var selection = new TransactionSelection(MemoContains: "SDK TEST");
var filtered = TransactionQueries.Select(normalized, selection);
var expected = data.Where(n => n.Children.SingleOrDefault(c => c.Name == "Memo")?.Value?.Contains(selection.MemoContains!, StringComparison.OrdinalIgnoreCase) == true)
    .Select(n => (Id: n.Children.Single(c => c.Name == "TxnID").Value, Type: n.Children.Single(c => c.Name == "TxnType").Value)).ToHashSet();
if (!expected.SetEquals(filtered.Select(n => ((string?)n.Id, (string?)n.Type)))) throw new Exception("Memo search changed the match set.");
File.WriteAllText(Path.Combine(fixtures, "retained-transaction-summaries-normalized.json"), JsonSerializer.Serialize(normalized, options));
var checks = new[] { new { records = normalized.Length, distinctTypes = normalized.Select(n => n.Type).Distinct().Count(), memoMatches = filtered.Count,
    everySummaryFieldPreserved = true, signedAmountsPreserved = true, memoMatchSetVerified = true,
    scope = "Offline normalization of retained successful TransactionQuery smoke data; not fresh execution of transaction list/search, pagination or date filters. Source query completeness is not inferred." } };
File.WriteAllText(Path.Combine(fixtures, "lifecycle-retained-transaction-normalization-verification.json"), JsonSerializer.Serialize(checks, options));
Console.WriteLine(JsonSerializer.Serialize(checks, options));
