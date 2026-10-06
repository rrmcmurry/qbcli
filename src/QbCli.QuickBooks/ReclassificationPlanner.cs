using System.Text.Json;
using System.Text.Json.Nodes;
using QbCli.Core;

namespace QbCli.QuickBooks;

public sealed record ReclassificationSelection(string AccountId, string ClassId, IReadOnlyList<string> TransactionTypes);
public sealed record ReclassificationChange(string TransactionType, string TxnId, string EditSequence,
    IReadOnlyList<string> LineIds, SdkElement Original, IReadOnlyList<SdkElement>? OriginalPostings = null);
public sealed record ReclassificationPlan(SdkPlan Plan, IReadOnlyList<ReclassificationChange> Changes, int ScannedTransactions,
    int AlreadyClassifiedLines, int OtherAccountLines, int ItemLinesOutsideScope);
public sealed record ReclassificationVerification(string TransactionType, string TxnId, bool ClassVerified, bool OtherFieldsPreserved,
    bool? PostingAccountsAndAmountsPreserved = null);

/// <summary>Documented expense and journal posting lines. Uses SDK identities and optimistic edit sequences.</summary>
public static class ReclassificationPlanner
{
    public static readonly string[] DefaultTypes = ["Check", "Bill", "CreditCardCharge", "CreditCardCredit", "VendorCredit", "ItemReceipt", "JournalEntry", "Deposit"];
    public static readonly string[] SupportedTypes = [..DefaultTypes,..SalesReclassificationPlanner.SupportedTypes];
    public static ReclassificationSelection Selection(JsonObject input)
    {
        if (input.Any(p => p.Key is not ("accountId" or "classId" or "transactionTypes"))) throw Usage("Unknown reclassification selection property.");
        string Id(string key) => input[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s : throw Usage(key + " requires a nonempty SDK ListID string.");
        var types = !input.ContainsKey("transactionTypes") ? DefaultTypes.ToArray()
            : input["transactionTypes"] is JsonArray a && a.All(v => v is JsonValue j && j.TryGetValue<string>(out _))
                ? a.Select(v => v!.GetValue<string>()).ToArray() : throw Usage("transactionTypes must be a string array.");
        if (types.Length == 0 || types.Distinct(StringComparer.Ordinal).Count() != types.Length || types.Any(t => !SupportedTypes.Contains(t, StringComparer.Ordinal)))
            throw Usage("transactionTypes must select unique supported posting-line transaction types.");
        return new(Id("accountId"), Id("classId"), types.Order(StringComparer.Ordinal).ToArray());
    }
    public static ReclassificationPlan Read(ISdkExecutor executor, ReclassificationSelection selection,
        string company, string protocol, string country, int pageSize, int batchSize)
    {
        // Validate selection references against the selected company before scanning.
        var references = executor.ExecuteSdk(new(protocol, country, "stopOnError", [
            new("account", "AccountQuery", new() { ["ListID"] = new JsonArray(selection.AccountId) }),
            new("class", "ClassQuery", new() { ["ListID"] = new JsonArray(selection.ClassId) })]), company, null);
        foreach (var call in references.Results)
            if (call.Outcome != "succeeded" || call.Data.Count != 1 || Value(call.Data[0], "ListID") != (call.RequestId == "account" ? selection.AccountId : selection.ClassId)
                || Value(call.Data[0], "IsActive") == "false")
                throw new QbException("selection_not_found", "Reclassification requires existing active account and class identities in the selected company.");
        if (references.Results.Count != 2 || !references.Success) throw new QbException("incomplete_response", "Reference resolution was incomplete.");
        var records = new Dictionary<string, IReadOnlyList<SdkElement>>(StringComparer.Ordinal);
        foreach (var type in selection.TransactionTypes)
        {
            var response = executor.QuerySdkAll(new(protocol, country, "stopOnError", [new("scan", type + "Query", QueryFields(type))]), company, pageSize);
            if (!response.Success || !response.Complete) throw new QbException("incomplete_response", "Reclassification requires complete transaction queries.");
            records.Add(type, response.Data);
        }
        SalesReclassificationSnapshot? sales=null;
        var itemTypes=selection.TransactionTypes.Where(t=>SalesReclassificationPlanner.SupportedTypes.Contains(t,StringComparer.Ordinal) || PurchaseItemReclassificationPlanner.SupportedTypes.Contains(t,StringComparer.Ordinal)).ToArray();
        if(itemTypes.Length>0)
        {
            var services=executor.QuerySdkAll(new(protocol,country,"stopOnError",[new("services","ItemServiceQuery",new() {["ActiveStatus"]="All"})]),company,pageSize);
            if(!services.Success || !services.Complete || services.Data.Any(n=>n.Name!="ItemServiceRet" || string.IsNullOrWhiteSpace(Value(n,"ListID"))))
                throw new QbException("incomplete_response","Sales reclassification requires a complete service-item identity query.");
            var serviceIds=services.Data.Select(n=>Value(n,"ListID")!).ToHashSet(StringComparer.Ordinal);
            if(serviceIds.Count!=services.Data.Count) throw new QbException("incomplete_response","Duplicate service-item identity.");
            var inventory=executor.QuerySdkAll(new(protocol,country,"stopOnError",[new("inventory","ItemInventoryQuery",new() {["ActiveStatus"]="All"})]),company,pageSize);
            if(!inventory.Success || !inventory.Complete || inventory.Data.Any(n=>n.Name!="ItemInventoryRet" || string.IsNullOrWhiteSpace(Value(n,"ListID"))))
                throw new QbException("incomplete_response","Sales reclassification requires a complete inventory-item identity query.");
            var inventoryIds=inventory.Data.Select(n=>Value(n,"ListID")!).ToHashSet(StringComparer.Ordinal);
            if(inventoryIds.Count!=inventory.Data.Count || inventoryIds.Overlaps(serviceIds)) throw new QbException("incomplete_response","Duplicate or conflicting inventory-item identity.");
            var knownIds=new HashSet<string>(serviceIds,StringComparer.Ordinal); knownIds.UnionWith(inventoryIds);
            HashSet<string> AdditionalItems(string kind)
            {
                var response=executor.QuerySdkAll(new(protocol,country,"stopOnError",[new("items",kind+"Query",new() {["ActiveStatus"]="All"})]),company,pageSize);
                if(!response.Success || !response.Complete || response.Data.Any(n=>n.Name!=kind+"Ret" || string.IsNullOrWhiteSpace(Value(n,"ListID"))))
                    throw new QbException("incomplete_response","Sales reclassification requires complete item-kind identity queries.");
                var ids=response.Data.Select(n=>Value(n,"ListID")!).ToHashSet(StringComparer.Ordinal);
                if(ids.Count!=response.Data.Count || ids.Overlaps(knownIds)) throw new QbException("incomplete_response","Duplicate or conflicting item-kind identity.");
                knownIds.UnionWith(ids); return ids;
            }
            var nonInventoryIds=AdditionalItems("ItemNonInventory");
            var otherChargeIds=AdditionalItems("ItemOtherCharge");
            var hasSales=selection.TransactionTypes.Any(t=>SalesReclassificationPlanner.SupportedTypes.Contains(t,StringComparer.Ordinal));
            var discountIds=hasSales?AdditionalItems("ItemDiscount"):null;
            var subtotalIds=hasSales?AdditionalItems("ItemSubtotal"):null;
            var paymentIds=hasSales?AdditionalItems("ItemPayment"):null;
            // AccountQuery has no iterator. Omitting MaxReturned reads all active
            // and inactive accounts, including historical posting references.
            var accounts=executor.ExecuteSdk(new(protocol,country,"stopOnError",[new("accounts","AccountQuery",new() {["ActiveStatus"]="All"})]),company,null);
            if(!accounts.Success || accounts.Results.Count!=1 || accounts.Results[0].Outcome is not ("succeeded" or "no_match"))
                throw new QbException("incomplete_response","Sales reclassification requires a complete account-type query.");
            var accountTypes=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var account in accounts.Results[0].Data)
                if(account.Name!="AccountRet" || string.IsNullOrWhiteSpace(Value(account,"ListID")) || string.IsNullOrWhiteSpace(Value(account,"AccountType"))
                    || !accountTypes.TryAdd(Value(account,"ListID")!,Value(account,"AccountType")!))
                    throw new QbException("incomplete_response","Missing or duplicate account-type identity.");
            var postings=executor.QuerySdkAll(new(protocol,country,"stopOnError",[new("postings","TransactionQuery",new()
            {
                ["TransactionTypeFilter"]=new JsonObject {["TxnTypeFilter"]=new JsonArray(itemTypes.Select(t=>(JsonNode)JsonValue.Create(t)!).ToArray())},
                ["TransactionDetailLevelFilter"]="AllExceptSummary"
            })]),company,pageSize);
            if(!postings.Success || !postings.Complete || postings.Data.Any(n=>n.Name!="TransactionRet"))
                throw new QbException("incomplete_response","Sales reclassification requires complete actual posting queries.");
            sales=new(serviceIds,postings.Data,inventoryIds,accountTypes,nonInventoryIds,otherChargeIds,discountIds,subtotalIds,paymentIds);
        }
        return Build(selection, records, protocol, country, batchSize,sales);
    }
    public static ReclassificationPlan Build(ReclassificationSelection selection,
        IReadOnlyDictionary<string, IReadOnlyList<SdkElement>> records, string protocol = "13.0", string country = "US", int batchSize = 50,
        SalesReclassificationSnapshot? sales = null)
    {
        var calls = new List<SdkCall>(); var changes = new List<ReclassificationChange>();
        int scanned = 0, classified = 0, other = 0, items = 0;
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in selection.TransactionTypes.Where(t=>!SalesReclassificationPlanner.SupportedTypes.Contains(t,StringComparer.Ordinal)).Order(StringComparer.Ordinal))
        {
            if (!SupportedTypes.Contains(type, StringComparer.Ordinal) || !records.TryGetValue(type, out var transactions)) throw Usage("Missing or unsupported transaction scan.");
            foreach (var record in transactions.OrderBy(r => Value(r, "TxnID"), StringComparer.Ordinal))
            {
                if (record.Name != type + "Ret") throw new QbException("incomplete_response", "Unexpected transaction record in scan.");
                scanned++;
                if(sales is null || !PurchaseItemReclassificationPlanner.SupportedTypes.Contains(type,StringComparer.Ordinal))
                    items += record.Children.Count(c => c.Name is "ItemLineRet" or "ItemGroupLineRet");
                var txnId = Required(record, "TxnID");
                if (!identities.Add(type + ":" + txnId)) throw new QbException("incomplete_response", "Duplicate transaction in scan.");
                var lines = new JsonArray(); var lineIds = new List<string>(); var seenLines = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in record.Children.Where(c => PostingLine(type,c)))
                {
                    string lineId = Required(line, "TxnLineID");
                    if (lineId is "0" or "-1" || !seenLines.Add(lineId)) throw new QbException("incomplete_response", "Invalid posting line identity.");
                    // The SDK replaces the supplied expense-line list. Retain every
                    // existing line, even those outside the selection. A line ID
                    // preserves its other fields; carry existing classes explicitly.
                    var retained = new JsonObject { ["TxnLineID"] = lineId };
                    string? account = RefId(line,"AccountRef");
                    if (string.IsNullOrWhiteSpace(account)) throw new QbException("incomplete_response","Cannot determine a posting account without its SDK ListID.");
                    if (type=="Deposit")
                    {
                        if (!string.IsNullOrWhiteSpace(Value(line,"PaymentTxnLineID")) && string.IsNullOrWhiteSpace(Value(line,"TxnID")))
                            throw new QbException("incomplete_response","A linked deposit line lacks its payment transaction identity.");
                        // Modify existing lines by their deposit TxnLineID. Sending
                        // PaymentTxnID re-submits a previously deposited payment.
                        // The unchanged AccountRef selects the required SDK choice.
                        retained["AccountRef"] = new JsonObject { ["ListID"] = account };
                    }
                    string? existingClass = RefId(line, "ClassRef");
                    var existingClassNode = line.Children.FirstOrDefault(c => c.Name == "ClassRef");
                    if (string.IsNullOrWhiteSpace(existingClass) && existingClassNode?.Children.Any(c => !string.IsNullOrWhiteSpace(c.Value)) == true)
                        throw new QbException("incomplete_response", "Cannot preserve an existing class without its SDK ListID.");
                    if (!string.IsNullOrWhiteSpace(existingClass)) retained["ClassRef"] = new JsonObject { ["ListID"] = existingClass };
                    lines.Add(retained);
                    if (account != selection.AccountId) { other++; continue; }
                    var cls = line.Children.FirstOrDefault(c => c.Name == "ClassRef");
                    if (cls is not null && cls.Children.Any(c => !string.IsNullOrWhiteSpace(c.Value))) { classified++; continue; }
                    lineIds.Add(lineId);
                    retained["ClassRef"] = new JsonObject { ["ListID"] = selection.ClassId };
                }
                if (lineIds.Count == 0) continue;
                string edit = Required(record, "EditSequence");
                calls.Add(new("reclass-" + (calls.Count + 1), type + "Mod", new() {
                    [type + "Mod"] = new JsonObject { ["TxnID"] = txnId, ["EditSequence"] = edit, [ModifierLine(type)] = lines } }));
                changes.Add(new(type, txnId, edit, lineIds, record));
            }
        }
        if(sales is not null)
        {
            var extra=PurchaseItemReclassificationPlanner.Build(selection,records,sales,protocol,country,batchSize);
            // Merge by transaction identity so expense and item edits use one
            // source EditSequence and one modifier, never two competing writes.
            var existing=changes.Select((c,i)=>(c,i)).ToDictionary(p=>(p.c.TransactionType,p.c.TxnId),p=>p.i);
            for(int i=0;i<extra.Changes.Count;i++)
            {
                var change=extra.Changes[i]; var call=extra.Plan.Requests[i];
                if(existing.TryGetValue((change.TransactionType,change.TxnId),out int index))
                {
                    var fields=calls[index].Fields.DeepClone().AsObject();
                    fields[change.TransactionType+"Mod"]!["$choices"]=call.Fields[change.TransactionType+"Mod"]!["$choices"]!.DeepClone();
                    calls[index]=calls[index] with {Fields=fields};
                    changes[index]=changes[index] with {LineIds=changes[index].LineIds.Concat(change.LineIds).ToArray(),OriginalPostings=change.OriginalPostings};
                }
                else {calls.Add(call);changes.Add(change);}
            }
            classified+=extra.AlreadyClassifiedLines;other+=extra.OtherAccountLines;items+=extra.ItemLinesOutsideScope;
        }
        var salesTypes=selection.TransactionTypes.Where(t=>SalesReclassificationPlanner.SupportedTypes.Contains(t,StringComparer.Ordinal)).ToArray();
        if(salesTypes.Length>0)
        {
            if(sales is null) throw Usage("Sales reclassification requires service-item and actual posting snapshots.");
            var extra=SalesReclassificationPlanner.Build(selection with {TransactionTypes=salesTypes},records,sales,protocol,country,batchSize);
            calls.AddRange(extra.Plan.Requests); changes.AddRange(extra.Changes);
            scanned+=extra.ScannedTransactions; classified+=extra.AlreadyClassifiedLines; other+=extra.OtherAccountLines; items+=extra.ItemLinesOutsideScope;
        }
        calls=calls.Select((c,i)=>c with {RequestId="reclass-"+(i+1)}).ToList();
        var plan = new SdkPlan(protocol, country, "stopOnError", calls, batchSize);
        if (calls.Count > 0) SdkBatch.Prepare(plan);
        return new(plan, changes, scanned, classified, other, items);
    }
    public static IReadOnlyList<ReclassificationVerification> Verify(ISdkExecutor executor, ReclassificationPlan plan,
        ReclassificationSelection selection, SdkExecutionResult applied, string company)
    {
        var successful = plan.Plan.Requests.Zip(plan.Changes).Where(p => applied.Results.Any(r => r.RequestId == p.First.RequestId && r.Outcome == "succeeded")).ToArray();
        if (successful.Length == 0) return [];
        var read = executor.ExecuteSdk(plan.Plan with { Requests = successful.Select(p => new SdkCall(p.First.RequestId, p.Second.TransactionType + "Query",
            QueryFields(p.Second.TransactionType,p.Second.TxnId))).ToArray() }, company, null);
        IReadOnlyList<SdkElement> salesPostings=[];
        var salesTypes=successful.Where(p=>p.Second.OriginalPostings is not null).Select(p=>p.Second.TransactionType).Distinct(StringComparer.Ordinal).ToArray();
        if(salesTypes.Length>0)
        {
            var postingRead=executor.QuerySdkAll(plan.Plan with {Requests=[new("verify-postings","TransactionQuery",new()
            {
                ["TransactionTypeFilter"]=new JsonObject {["TxnTypeFilter"]=new JsonArray(salesTypes.Select(t=>(JsonNode)JsonValue.Create(t)!).ToArray())},
                ["TransactionDetailLevelFilter"]="AllExceptSummary"
            })]},company,500);
            if(!postingRead.Success || !postingRead.Complete) throw new QbException("incomplete_response","Sales posting verification is incomplete.");
            salesPostings=postingRead.Data;
        }
        var postingIndex=salesPostings.GroupBy(n=>(Value(n,"TxnType"),Value(n,"TxnID"))).ToDictionary(g=>g.Key,g=>g.ToArray());
        return successful.Select(pair =>
        {
            var outcome = read.Results.SingleOrDefault(r => r.RequestId == pair.First.RequestId);
            var current = outcome?.Outcome == "succeeded" && outcome.Data.Count == 1 ? outcome.Data[0] : null;
            var verified=Check(pair.Second, current, selection.ClassId);
            if(pair.Second.OriginalPostings is not null)
            {
                bool preserved=SalesReclassificationPlanner.PostingsPreserved(pair.Second.OriginalPostings,postingIndex.GetValueOrDefault((pair.Second.TransactionType,pair.Second.TxnId))??[]);
                verified=verified with {OtherFieldsPreserved=verified.OtherFieldsPreserved && preserved,PostingAccountsAndAmountsPreserved=preserved};
            }
            return verified;
        }).ToArray();
    }
    public static ReclassificationVerification Check(ReclassificationChange change, SdkElement? current, string classId)
    {
        if(SalesReclassificationPlanner.SupportedTypes.Contains(change.TransactionType,StringComparer.Ordinal)) return SalesReclassificationPlanner.Check(change,current,classId);
        if(PurchaseItemReclassificationPlanner.SupportedTypes.Contains(change.TransactionType,StringComparer.Ordinal) && change.OriginalPostings is not null)
            return PurchaseItemReclassificationPlanner.Check(change,current,classId);
        bool classes = current is not null && change.LineIds.All(id => current.Children.Count(c => PostingLine(change.TransactionType,c) && Value(c, "TxnLineID") == id && RefId(c, "ClassRef") == classId) == 1);
        bool preserved = current is not null && JsonSerializer.Serialize(Normalize(change.Original, change.LineIds,change.TransactionType)) == JsonSerializer.Serialize(Normalize(current, change.LineIds,change.TransactionType));
        return new(change.TransactionType, change.TxnId, classes, preserved);
    }
    private static JsonObject QueryFields(string type,string? txnId=null)
    {
        var fields=new JsonObject {["IncludeLineItems"]=true};
        if(txnId is not null) fields["TxnID"]=new JsonArray(txnId);
        if(type is "Invoice" or "CreditMemo" or "Check" or "Bill" or "VendorCredit" or "ItemReceipt") fields["IncludeLinkedTxns"]=true;
        return fields;
    }
    private static string ModifierLine(string type)=>type switch {"JournalEntry"=>"JournalLineMod","Deposit"=>"DepositLineMod",_=>"ExpenseLineMod"};
    private static bool PostingLine(string type,SdkElement element)=>type switch
    {"JournalEntry"=>element.Name is "JournalDebitLine" or "JournalCreditLine","Deposit"=>element.Name=="DepositLineRet",_=>element.Name=="ExpenseLineRet"};
    private static SdkElement Normalize(SdkElement record, IReadOnlyList<string> modifiedLines,string type) => record with
    {
        Children = record.Children.Where(c => c.Name is not ("TimeModified" or "EditSequence")).Select(c => PostingLine(type,c) && modifiedLines.Contains(Value(c, "TxnLineID")!, StringComparer.Ordinal)
            ? c with { Children = c.Children.Where(f => f.Name != "ClassRef").ToArray() } : c).ToArray()
    };
    private static string? RefId(SdkElement e, string name) => e.Children.FirstOrDefault(c => c.Name == name)?.Children.FirstOrDefault(c => c.Name == "ListID")?.Value;
    private static string? Value(SdkElement e, string name) => e.Children.FirstOrDefault(c => c.Name == name)?.Value;
    private static string Required(SdkElement e, string name) => !string.IsNullOrWhiteSpace(Value(e, name)) ? Value(e, name)! : throw new QbException("incomplete_response", "Transaction scan lacks required identity metadata.");
    private static QbException Usage(string message) => new("invalid_arguments", message);
}
