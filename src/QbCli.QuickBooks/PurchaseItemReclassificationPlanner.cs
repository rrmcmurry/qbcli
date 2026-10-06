using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using QbCli.Core;

namespace QbCli.QuickBooks;

/// <summary>Retains every purchase item when selecting confirmed single-posting leaves.</summary>
public static class PurchaseItemReclassificationPlanner
{
    public static readonly string[] SupportedTypes=["Check","Bill","CreditCardCharge","CreditCardCredit","VendorCredit","ItemReceipt"];
    public static ReclassificationPlan Build(ReclassificationSelection selection,IReadOnlyDictionary<string,IReadOnlyList<SdkElement>> records,
        SalesReclassificationSnapshot snapshot,string protocol,string country,int batchSize)
    {
        var calls=new List<SdkCall>(); var changes=new List<ReclassificationChange>();
        int classified=0,other=0,outside=0;
        var postingIndex=snapshot.Postings.GroupBy(p=>(Required(p,"TxnType"),Required(p,"TxnID"))).ToDictionary(g=>g.Key,g=>g.ToArray());
        foreach(var type in selection.TransactionTypes.Where(t=>SupportedTypes.Contains(t,StringComparer.Ordinal)))
        foreach(var record in records[type])
        {
            string txn=Required(record,"TxnID");
            var posts=postingIndex.GetValueOrDefault((type,txn))??[];
            var linePosts=posts.GroupBy(p=>Required(p,"TxnLineID"),StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
            var leaves=Leaves(record).ToArray(); var seen=new HashSet<string>(StringComparer.Ordinal); var selected=new HashSet<string>(StringComparer.Ordinal);
            foreach(var leaf in leaves)
            {
                string id=LineId(leaf); if(!seen.Add(id)) throw Invalid("Duplicate purchase item identity.");
                if(!Known(leaf,snapshot)) outside++;
                var joined=linePosts.GetValueOrDefault(id)??[];
                if(!joined.Any(p=>Reference(p,"AccountRef")==selection.AccountId)) {other++;continue;}
                if(Reference(leaf,"ClassRef") is not null) {classified++;continue;}
                var primary=Posting(leaf,joined,snapshot);
                if(Reference(primary,"AccountRef")!=selection.AccountId) {other++;continue;}
                selected.Add(id);
            }
            if(selected.Count==0) continue;
            var retained=new Dictionary<string,JsonObject>(StringComparer.Ordinal);
            foreach(var leaf in leaves)
            {
                string id=LineId(leaf); var post=Posting(leaf,linePosts.GetValueOrDefault(id)??[],snapshot);
                var body=new JsonObject {["TxnLineID"]=id,["OverrideItemAccountRef"]=new JsonObject {["ListID"]=Reference(post,"AccountRef")}};
                string? cls=selected.Contains(id)?selection.ClassId:Reference(leaf,"ClassRef");
                if(cls is not null) body["ClassRef"]=new JsonObject {["ListID"]=cls};
                retained.Add(id,body);
            }
            var choices=new JsonArray();
            // Expense and item IDs share the transaction's identity namespace.
            foreach(var expense in record.Children.Where(n=>n.Name=="ExpenseLineRet"))
                if(!seen.Add(LineId(expense))) throw Invalid("Purchase expense/item identities conflict.");
            foreach(var node in record.Children)
            {
                if(node.Name=="ItemLineRet") choices.Add(new JsonObject {["ItemLineMod"]=retained[LineId(node)]});
                else if(node.Name=="ItemGroupLineRet")
                {
                    string group=LineId(node); if(!seen.Add(group)) throw Invalid("Duplicate purchase group identity.");
                    var children=node.Children.Where(c=>c.Name=="ItemLineRet").ToArray();
                    if(children.Length==0 || node.Children.Any(c=>c.Name=="ItemGroupLineRet")) throw Invalid("Empty or nested purchase group is unsupported.");
                    choices.Add(new JsonObject {["ItemGroupLineMod"]=new JsonObject {["TxnLineID"]=group,["ItemLineMod"]=new JsonArray(children.Select(c=>(JsonNode)retained[LineId(c)]).ToArray())}});
                }
                else if(node.Name.StartsWith("Item",StringComparison.Ordinal) && node.Name.EndsWith("LineRet",StringComparison.Ordinal)) throw Invalid("Unsupported purchase item structure.");
            }
            string edit=Required(record,"EditSequence");
            calls.Add(new("purchase-"+(calls.Count+1),type+"Mod",new() {[type+"Mod"]=new JsonObject {["TxnID"]=txn,["EditSequence"]=edit,["$choices"]=choices}}));
            changes.Add(new(type,txn,edit,leaves.Select(LineId).Where(selected.Contains).ToArray(),record,posts));
        }
        var plan=new SdkPlan(protocol,country,"stopOnError",calls,batchSize);
        if(calls.Count>0) SdkBatch.Prepare(plan);
        return new(plan,changes,0,classified,other,outside);
    }
    public static ReclassificationVerification Check(ReclassificationChange change,SdkElement? current,string classId)
    {
        var selected=change.LineIds.ToHashSet(StringComparer.Ordinal);
        IEnumerable<SdkElement> ClassLines(SdkElement n)=>n.Children.Where(c=>c.Name=="ExpenseLineRet").Concat(Leaves(n));
        var returned=current is null?[]:ClassLines(current).ToArray();
        bool classes=selected.All(id=>returned.Count(n=>Scalar(n,"TxnLineID")==id && Reference(n,"ClassRef")==classId)==1);
        SdkElement Normalize(SdkElement n)=>n with {Children=n.Children.Where(c=>c.Name is not ("TimeModified" or "EditSequence")
            && !(c.Name=="ClassRef" && (n.Name is "ExpenseLineRet" or "ItemLineRet") && selected.Contains(Scalar(n,"TxnLineID")??""))).Select(Normalize).ToArray()};
        bool fields=current is not null && JsonSerializer.Serialize(Normalize(change.Original))==JsonSerializer.Serialize(Normalize(current));
        return new(change.TransactionType,change.TxnId,classes,fields);
    }
    private static IEnumerable<SdkElement> Leaves(SdkElement root)=>root.Children.Where(c=>c.Name=="ItemLineRet")
        .Concat(root.Children.Where(c=>c.Name=="ItemGroupLineRet").SelectMany(g=>g.Children.Where(c=>c.Name=="ItemLineRet")));
    private static bool Known(SdkElement leaf,SalesReclassificationSnapshot snapshot)=>Reference(leaf,"ItemRef") is {} id
        && (snapshot.ServiceItemIds.Contains(id) || snapshot.NonInventoryItemIds?.Contains(id)==true || snapshot.OtherChargeItemIds?.Contains(id)==true || snapshot.InventoryItemIds?.Contains(id)==true);
    private static SdkElement Posting(SdkElement leaf,SdkElement[] joined,SalesReclassificationSnapshot snapshot)
    {
        decimal Amount(SdkElement n)=>decimal.TryParse(Scalar(n,"Amount"),NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var a)?a:throw Invalid("Invalid purchase posting amount.");
        if(!Known(leaf,snapshot) || joined.Length==0 || joined.Any(p=>Reference(p,"AccountRef") is null)) throw Invalid("Purchase item lacks confirmed actual postings.");
        if(snapshot.InventoryItemIds?.Contains(Reference(leaf,"ItemRef")!)==true)
        {
            string? Kind(SdkElement p)=>snapshot.AccountTypes?.GetValueOrDefault(Reference(p,"AccountRef")!);
            var candidates=joined.Where(p=>Kind(p)=="OtherCurrentAsset" && Math.Abs(Amount(p))==Math.Abs(Amount(leaf))).ToArray();
            if(candidates.Length!=1) throw Invalid("Inventory purchase lacks one amount-matched asset posting.");
            var primary=candidates[0];
            if(joined.Length==1) return primary;
            var adjustments=joined.Where(p=>!ReferenceEquals(p,primary)).ToArray();
            if(joined.Length!=3 || Required(primary,"TxnType") is not ("CreditCardCredit" or "VendorCredit")
                || adjustments.Count(p=>Kind(p)=="OtherCurrentAsset" && Reference(p,"AccountRef")==Reference(primary,"AccountRef"))!=1
                || adjustments.Count(p=>Kind(p)=="CostOfGoodsSold")!=1
                || adjustments.Sum(Amount)!=0) throw Invalid("Unverified inventory purchase cost-adjustment shape.");
            return primary;
        }
        if(joined.Length!=1) throw Invalid("Purchase item lacks one confirmed service/non-inventory/other-charge posting.");
        if(Math.Abs(Amount(leaf))!=Math.Abs(Amount(joined[0]))) throw Invalid("Purchase item/posting amounts do not match.");
        return joined[0];
    }
    private static string LineId(SdkElement n)=>Required(n,"TxnLineID") is {} id && id is not ("0" or "-1")?id:throw Invalid("Invalid existing purchase line identity.");
    private static SdkElement? Single(SdkElement n,string name)
    {
        var values=n.Children.Where(c=>c.Name==name).ToArray(); if(values.Length>1) throw Invalid("Duplicate purchase field."); return values.SingleOrDefault();
    }
    private static string? Scalar(SdkElement n,string name) {var value=Single(n,name);if(value?.Children.Count>0) throw Invalid("Expected scalar purchase field.");return value?.Value;}
    private static string Required(SdkElement n,string name)=>!string.IsNullOrWhiteSpace(Scalar(n,name))?Scalar(n,name)!:throw Invalid("Missing purchase identity.");
    private static string? Reference(SdkElement n,string name)
    {
        var value=Single(n,name);if(value is null)return null;string? id=Scalar(value,"ListID");
        if(!string.IsNullOrWhiteSpace(id))return id;
        if(value.Value is not null || value.Children.Any(c=>!string.IsNullOrWhiteSpace(c.Value)))throw Invalid("Cannot preserve purchase reference without ListID.");return null;
    }
    private static QbException Invalid(string message)=>new("incomplete_response",message);
}
