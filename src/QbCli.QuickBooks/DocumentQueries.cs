using System.Globalization;
using System.Text.Json.Nodes;
using QbCli.Core;

namespace QbCli.QuickBooks;

public static class DocumentQueries
{
    public static SdkPlan Plan(DocumentKind kind, bool openOnly, string country)
    {
        string name = Name(kind);
        return new("13.0", country, "stopOnError", [new("documents", name + "Query", new JsonObject
        {
            ["PaidStatus"] = openOnly ? "NotPaidOnly" : "All",
            ["IncludeLineItems"] = false, ["IncludeLinkedTxns"] = false
        })]);
    }

    public static string Name(DocumentKind kind) => kind switch
    {
        DocumentKind.Invoice => "Invoice", DocumentKind.Bill => "Bill",
        _ => throw new QbException("invalid_arguments", "Unknown document type.")
    };

    public static SdkPlan BillBalancesPlan(IEnumerable<DocumentSummary> records,string country)=>new("13.0",country,"stopOnError",
        records.Where(r=>r.Paid!=true).Select(r=>r.Party.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
        .Select((id,i)=>new SdkCall("bill-balances-"+i,"BillToPayQuery",new JsonObject {["PayeeEntityRef"]=new JsonObject {["ListID"]=id}})).ToArray());

    public static IReadOnlyDictionary<string,decimal> BillBalances(SdkExecutionResult result)
    {
        if(!result.Success || result.Results.Any(r=>r.Outcome is not ("succeeded" or "no_match"))) throw Invalid();
        var balances=new Dictionary<string,decimal>(StringComparer.Ordinal);
        foreach(var row in result.Results)
        foreach(var record in row.Data)
        {
            if(record.Name!="BillToPayRet" || record.Children.Count!=1)throw Invalid();
            var detail=record.Children[0];
            if(detail.Name=="CreditToApply")continue;
            if(detail.Name!="BillToPay")throw Invalid();
            string Required(string name)
            {
                var values=detail.Children.Where(c=>c.Name==name).ToArray();
                if(values.Length!=1 || values[0].Children.Count!=0 || string.IsNullOrWhiteSpace(values[0].Value))throw Invalid();
                return values[0].Value!;
            }
            if(Required("TxnType")!="Bill")continue;
            string id=Required("TxnID");
            if(!decimal.TryParse(Required("AmountDue"),NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)
                || !balances.TryAdd(id,amount))throw Invalid();
        }
        return balances;
    }

    public static DocumentSummary Normalize(SdkElement record, DocumentKind kind, bool openOnly,decimal? billRemainingAmount=null)
    {
        if (record.Name != Name(kind) + "Ret") throw Invalid();
        string? Scalar(SdkElement node, string name, bool required = false)
        {
            var fields = node.Children.Where(c => c.Name == name).ToArray();
            if (fields.Length > 1 || fields.Any(f => f.Children.Count != 0) || required && (fields.Length == 0 || string.IsNullOrWhiteSpace(fields[0].Value))) throw Invalid();
            return fields.SingleOrDefault()?.Value;
        }
        SdkElement? Aggregate(string name, bool required = false)
        {
            var fields = record.Children.Where(c => c.Name == name).ToArray();
            if (fields.Length > 1 || fields.Any(f => f.Value is not null) || required && fields.Length == 0) throw Invalid();
            return fields.SingleOrDefault();
        }
        string? Date(string name, bool required = false)
        {
            var value = Scalar(record, name, required);
            if (value is not null && !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw Invalid();
            return value;
        }
        var party = Aggregate(kind == DocumentKind.Invoice ? "CustomerRef" : "VendorRef", true)!;
        var currency = Aggregate("CurrencyRef");
        var amountText = kind==DocumentKind.Invoice ? Scalar(record,"BalanceRemaining") : null;
        decimal? amount = null;
        if (amountText is not null)
        {
            if (!decimal.TryParse(amountText, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)) throw Invalid();
            amount = parsed;
        }
        bool? paid = Scalar(record, "IsPaid") switch
        {
            null => null, "true" or "1" => true, "false" or "0" => false, _ => throw Invalid()
        };
        // BillQuery OpenAmount returned the vendor total for distinct bills on
        // the live host. Resolve per-bill outstanding amounts from BillToPayQuery.
        // BillQuery AmountDue is the original total and is never substituted.
        if(kind==DocumentKind.Bill)amount=paid==true?0:billRemainingAmount;
        if (openOnly && (paid == true || amount == 0)) throw Invalid();
        return new(Scalar(record, "TxnID", true)!, Scalar(record, "RefNumber"), Date("TxnDate", true)!, Date("DueDate"),
            new(Scalar(party, "ListID", true)!, Scalar(party, "FullName")), amount, paid, Scalar(record, "Memo"),
            currency is null ? null : new(Scalar(currency, "ListID"), Scalar(currency, "FullName")));
    }
    private static QbException Invalid() => new("invalid_response", "The SDK document response has a missing, duplicate, malformed or inconsistent summary field.");
}
