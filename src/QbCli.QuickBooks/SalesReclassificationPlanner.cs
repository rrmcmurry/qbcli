using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbCli.Core;

namespace QbCli.QuickBooks;

public sealed record SalesReclassificationSnapshot(IReadOnlySet<string> ServiceItemIds, IReadOnlyList<SdkElement> Postings,
    IReadOnlySet<string>? InventoryItemIds = null, IReadOnlyDictionary<string,string>? AccountTypes = null,
    IReadOnlySet<string>? NonInventoryItemIds = null, IReadOnlySet<string>? OtherChargeItemIds = null,
    IReadOnlySet<string>? DiscountItemIds = null, IReadOnlySet<string>? SubtotalItemIds = null,
    IReadOnlySet<string>? PaymentItemIds = null);

/// <summary>Plans sales line classes using confirmed item kinds and actual posting accounts.</summary>
public static class SalesReclassificationPlanner
{
    public static readonly string[] SupportedTypes=["Invoice","SalesReceipt","CreditMemo"];

    public static ReclassificationPlan Build(ReclassificationSelection selection,
        IReadOnlyDictionary<string,IReadOnlyList<SdkElement>> records, SalesReclassificationSnapshot snapshot,
        string protocol="13.0",string country="US",int batchSize=50)
    {
        var calls=new List<SdkCall>(); var changes=new List<ReclassificationChange>();
        int scanned=0,classified=0,other=0,outside=0;
        var seenTransactions=new HashSet<string>(StringComparer.Ordinal);
        // Index once: a large company must not rescan every posting for each transaction.
        var postingIndex=snapshot.Postings.GroupBy(p=>(Required(p,"TxnType"),Required(p,"TxnID")))
            .ToDictionary(g=>g.Key,g=>g.ToArray());
        foreach(var type in selection.TransactionTypes.Order(StringComparer.Ordinal))
        {
            if(!SupportedTypes.Contains(type,StringComparer.Ordinal) || !records.TryGetValue(type,out var transactions)) throw Invalid("Missing sales transaction scan.");
            foreach(var record in transactions.OrderBy(r=>Scalar(r,"TxnID"),StringComparer.Ordinal))
            {
                if(record.Name!=type+"Ret") throw Invalid("Unexpected sales transaction record.");
                string txn=Required(record,"TxnID"); scanned++;
                if(!seenTransactions.Add(type+":"+txn)) throw Invalid("Duplicate sales transaction.");
                var postings=postingIndex.GetValueOrDefault((type,txn))??[];
                var postingLines=postings.GroupBy(p=>Required(p,"TxnLineID"),StringComparer.Ordinal)
                    .ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
                var leaves=Leaves(record,type).ToArray();
                var seenLines=new HashSet<string>(StringComparer.Ordinal);
                foreach(var leaf in leaves)
                    if(!seenLines.Add(LineId(leaf))) throw Invalid("Duplicate sales leaf identity.");
                var joins=new Dictionary<string,SdkElement[]>(StringComparer.Ordinal);
                var selected=new List<string>();
                foreach(var leaf in leaves)
                {
                    string id=LineId(leaf);
                    var joined=postingLines.GetValueOrDefault(id)??[]; joins.Add(id,joined);
                    string? cls=Reference(leaf,"ClassRef");
                    bool target=joined.Any(p=>Reference(p,"AccountRef")==selection.AccountId);
                    if(!SinglePostingItem(Reference(leaf,"ItemRef")??"",snapshot) && !(snapshot.InventoryItemIds?.Contains(Reference(leaf,"ItemRef")??"")??false)
                        && !(snapshot.SubtotalItemIds?.Contains(Reference(leaf,"ItemRef")??"")??false) && !DescriptionOnly(leaf)) outside++;
                    if(!target) {other++;continue;}
                    if(cls is not null) {classified++;continue;}
                    var revenue=RevenuePosting(leaf,joined,snapshot);
                    if(Reference(revenue,"AccountRef")!=selection.AccountId)
                        throw Invalid("Inventory class selection by an asset or COGS account is outside the verified revenue-account recipe.");
                    selected.Add(id);
                }
                if(selected.Count==0) continue;
                var selectedIds=selected.ToHashSet(StringComparer.Ordinal);
                // Every retained leaf needs the verified preservation recipe, not
                // just leaves selected for a class change. Never infer item defaults.
                var retained=new Dictionary<string,JsonObject>(StringComparer.Ordinal);
                foreach(var leaf in leaves)
                {
                    string id=LineId(leaf); var joined=joins[id];
                    if(snapshot.SubtotalItemIds?.Contains(Reference(leaf,"ItemRef")??"")==true || DescriptionOnly(leaf))
                    {
                        if(joined.Any(p=>Reference(p,"AccountRef") is not null)) throw Invalid("A nonposting subtotal/description unexpectedly has an account posting.");
                        var nonposting=new JsonObject {["TxnLineID"]=id};
                        if(Reference(leaf,"ClassRef") is {} existingClass) nonposting["ClassRef"]=new JsonObject {["ListID"]=existingClass};
                        retained.Add(id,nonposting);continue;
                    }
                    var revenue=RevenuePosting(leaf,joined,snapshot);
                    string account=Reference(revenue,"AccountRef")!;
                    var body=new JsonObject {["TxnLineID"]=id,["OverrideItemAccountRef"]=new JsonObject {["ListID"]=account}};
                    string? cls=selectedIds.Contains(id)?selection.ClassId:Reference(leaf,"ClassRef");
                    if(cls is not null) body["ClassRef"]=new JsonObject {["ListID"]=cls};
                    retained.Add(id,body);
                }
                var choices=new JsonArray();
                var allIds=new HashSet<string>(seenLines,StringComparer.Ordinal);
                foreach(var node in record.Children)
                {
                    if(node.Name==type+"LineRet") choices.Add(new JsonObject {[type+"LineMod"]=retained[LineId(node)]});
                    else if(node.Name==type+"LineGroupRet")
                    {
                        string group=LineId(node);
                        if(!allIds.Add(group)) throw Invalid("Duplicate sales group identity.");
                        var children=new JsonArray(node.Children.Where(c=>c.Name==type+"LineRet").Select(c=>(JsonNode)retained[LineId(c)]).ToArray());
                        if(children.Count==0 || node.Children.Any(c=>c.Name.EndsWith("LineGroupRet",StringComparison.Ordinal))) throw Invalid("Unsupported empty or nested sales group.");
                        choices.Add(new JsonObject {[type+"LineGroupMod"]=new JsonObject {["TxnLineID"]=group,[type+"LineMod"]=children}});
                    }
                    else if(node.Name.EndsWith("LineRet",StringComparison.Ordinal)) throw Invalid("Unsupported sales line structure.");
                }
                string edit=Required(record,"EditSequence");
                calls.Add(new("reclass-"+(calls.Count+1),type+"Mod",new JsonObject {[type+"Mod"]=new JsonObject
                    {["TxnID"]=txn,["EditSequence"]=edit,["$choices"]=choices}}));
                changes.Add(new(type,txn,edit,selected,record,postings));
            }
        }
        var plan=new SdkPlan(protocol,country,"stopOnError",calls,batchSize);
        if(calls.Count>0) SdkBatch.Prepare(plan);
        return new(plan,changes,scanned,classified,other,outside);
    }

    public static ReclassificationVerification Check(ReclassificationChange change,SdkElement? current,string classId)
    {
        var leaves=current is null?[]:Leaves(current,change.TransactionType).ToArray();
        var lines=leaves.GroupBy(l=>Required(l,"TxnLineID"),StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
        bool classes=change.LineIds.All(id=>lines.TryGetValue(id,out var matching) && matching.Length==1 && Reference(matching[0],"ClassRef")==classId);
        bool preserved=current is not null && CanonicalForm(change.Original,change.LineIds,change.TransactionType)==CanonicalForm(current,change.LineIds,change.TransactionType);
        return new(change.TransactionType,change.TxnId,classes,preserved);
    }

    public static bool PostingsPreserved(IReadOnlyList<SdkElement> original,IReadOnlyList<SdkElement> current)
    {
        string Key(SdkElement n)=>JsonSerializer.Serialize(n with {Children=n.Children.Where(c=>c.Name!="TimeModified").ToArray()});
        return original.Select(Key).Order(StringComparer.Ordinal).SequenceEqual(current.Select(Key).Order(StringComparer.Ordinal));
    }
    private static SdkElement RevenuePosting(SdkElement leaf,SdkElement[] joined,SalesReclassificationSnapshot snapshot)
    {
        string item=Reference(leaf,"ItemRef")??"";
        SdkElement revenue;
        if(SinglePostingItem(item,snapshot))
        {
            if(joined.Length!=1 || Reference(joined[0],"AccountRef") is null)
                throw Invalid("A service, non-inventory, other-charge, discount or payment sales leaf lacks one confirmed posting account.");
            revenue=joined[0];
        }
        else if(snapshot.InventoryItemIds?.Contains(item)==true)
        {
            // Resolve actual historical accounts by their confirmed SDK account
            // types, never by current item defaults or arbitrary posting order.
            if(joined.Length!=3 || snapshot.AccountTypes is null) throw Invalid("An inventory sales leaf requires three confirmed income/asset/COGS postings.");
            var accounts=joined.Select(p=>Reference(p,"AccountRef")).ToArray();
            if(accounts.Any(a=>a is null || !snapshot.AccountTypes.ContainsKey(a)) || accounts.Distinct(StringComparer.Ordinal).Count()!=3)
                throw Invalid("Inventory posting accounts are missing, duplicated or unknown.");
            SdkElement ByType(string type)
            {
                var matches=joined.Where(p=>snapshot.AccountTypes[Reference(p,"AccountRef")!]==type).ToArray();
                return matches.Length==1?matches[0]:throw Invalid("Inventory postings do not have one confirmed Income, OtherCurrentAsset and CostOfGoodsSold account.");
            }
            revenue=ByType("Income"); var asset=ByType("OtherCurrentAsset"); var cogs=ByType("CostOfGoodsSold");
            if(Amount(asset)+Amount(cogs)!=0) throw Invalid("Inventory asset and COGS posting amounts do not balance.");
        }
        else throw Invalid("A changed sales transaction contains an item outside the verified service/non-inventory/other-charge/discount/payment/inventory recipes.");
        if(Math.Abs(Amount(leaf))!=Math.Abs(Amount(revenue))) throw Invalid("Sales leaf and revenue posting amounts do not match.");
        return revenue;
    }
    private static bool SinglePostingItem(string item,SalesReclassificationSnapshot snapshot)=>snapshot.ServiceItemIds.Contains(item)
        || snapshot.NonInventoryItemIds?.Contains(item)==true || snapshot.OtherChargeItemIds?.Contains(item)==true || snapshot.DiscountItemIds?.Contains(item)==true
        || snapshot.PaymentItemIds?.Contains(item)==true;
    private static bool DescriptionOnly(SdkElement leaf)
    {
        if(Single(leaf,"ItemRef") is not null || string.IsNullOrWhiteSpace(Scalar(leaf,"Desc")) || Single(leaf,"OverrideItemAccountRef") is not null)return false;
        foreach(var field in new[]{"Amount","Quantity","Rate","RatePercent"})
            if(Scalar(leaf,field) is {} text && (!decimal.TryParse(text,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var number) || number!=0))return false;
        return true;
    }
    private static string CanonicalForm(SdkElement root,IReadOnlyList<string> selected,string type)
    {
        var selectedIds=selected.ToHashSet(StringComparer.Ordinal);
        SdkElement Normalize(SdkElement n)=>n with {Children=n.Children.Where(c=>c.Name is not ("EditSequence" or "TimeModified")
            && !(c.Name=="ClassRef" && n.Name==type+"LineRet" && selectedIds.Contains(Scalar(n,"TxnLineID")!))).Select(Normalize).ToArray()};
        return JsonSerializer.Serialize(Normalize(root));
    }
    private static IEnumerable<SdkElement> Leaves(SdkElement root,string type)=>root.Children.Where(c=>c.Name==type+"LineRet")
        .Concat(root.Children.Where(c=>c.Name==type+"LineGroupRet").SelectMany(g=>g.Children.Where(c=>c.Name==type+"LineRet")));
    private static string LineId(SdkElement n)=>Required(n,"TxnLineID") is { } id && id is not ("0" or "-1")?id:throw Invalid("Invalid existing sales line identity.");
    private static SdkElement? Single(SdkElement n,string name)
    {
        var values=n.Children.Where(c=>c.Name==name).ToArray();
        if(values.Length>1) throw Invalid("Duplicate sales field.");
        return values.SingleOrDefault();
    }
    private static string? Scalar(SdkElement n,string name)
    {
        var value=Single(n,name);
        if(value?.Children.Count>0) throw Invalid("Expected scalar sales field.");
        return value?.Value;
    }
    private static string Required(SdkElement n,string name)=>!string.IsNullOrWhiteSpace(Scalar(n,name))?Scalar(n,name)!:throw Invalid("Missing required sales identity.");
    private static string? Reference(SdkElement n,string name)
    {
        var value=Single(n,name); if(value is null) return null;
        string? id=Scalar(value,"ListID");
        if(string.IsNullOrWhiteSpace(id))
        {
            if(value.Value is not null || value.Children.Any(c=>!string.IsNullOrWhiteSpace(c.Value))) throw Invalid("Cannot preserve a sales reference without its ListID.");
            return null;
        }
        return id;
    }
    private static decimal Amount(SdkElement n)=>decimal.TryParse(Scalar(n,"Amount"),NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture,out var amount)?amount:throw Invalid("Missing or invalid sales posting amount.");
    private static QbException Invalid(string message)=>new("incomplete_response",message);
}
