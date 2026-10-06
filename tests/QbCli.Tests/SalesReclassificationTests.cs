using System.Text.Json.Nodes;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class SalesReclassificationTests
{
    private static ReclassificationPlan DescriptionPlan(string type,string failure="")
    {
        var description=N(type+"LineRet",null,N("TxnLineID","desc"),N("Desc","preserve description"),Ref("ClassRef","existing"));
        if(failure=="amount")description=description with {Children=[..description.Children,N("Amount","9.00")]};
        if(failure=="quantity")description=description with {Children=[..description.Children,N("Quantity","1")]};
        if(failure=="rate")description=description with {Children=[..description.Children,N("Rate","1")]};
        if(failure=="empty-item")description=description with {Children=[..description.Children,N("ItemRef")]};
        if(failure=="blank")description=description with {Children=description.Children.Select(n=>n.Name=="Desc"?N("Desc"," "):n).ToArray()};
        var form=N(type+"Ret",null,N("TxnID","txn"),N("EditSequence","edit"),Line(type,"flat"),description);
        var posts=new List<SdkElement>{Post(type,"flat")};
        if(failure=="posting")posts.Add(Post(type,"desc","other-account"));
        var snapshot=new SalesReclassificationSnapshot(new HashSet<string>{"service"},posts);
        return SalesReclassificationPlanner.Build(new("account","target",[type]),new Dictionary<string,IReadOnlyList<SdkElement>>{[type]=[form]},snapshot);
    }
    [Theory] [InlineData("Invoice")] [InlineData("SalesReceipt")] [InlineData("CreditMemo")]
    public void DescriptionOnlyLineRetainsItsIdentityAndExistingClassWithoutAnAccountOverride(string type)
    {
        var plan=DescriptionPlan(type);Assert.Equal(new[]{"flat"},plan.Changes.Single().LineIds);
        var body=plan.Plan.Requests.Single().Fields[type+"Mod"]!["$choices"]![1]![type+"LineMod"]!;
        Assert.Equal("desc",body["TxnLineID"]!.GetValue<string>());
        Assert.Equal("existing",body["ClassRef"]!["ListID"]!.GetValue<string>());
        Assert.Null(body["OverrideItemAccountRef"]);Assert.Null(body["Desc"]);
        Assert.Equal(0,plan.ItemLinesOutsideScope);
    }
    [Theory] [InlineData("amount")] [InlineData("quantity")] [InlineData("rate")]
    [InlineData("empty-item")] [InlineData("blank")] [InlineData("posting")]
    public void MaterialOrUnidentifiedItemlessLinesCannotUseTheDescriptionRecipe(string failure)=>Assert.Throws<QbException>(()=>DescriptionPlan("Invoice",failure));
    private static ReclassificationPlan PaymentPlan(string type,string failure="")
    {
        var form=N(type+"Ret",null,N("TxnID","txn"),N("EditSequence","edit"),Line(type,"service"),Line(type,"payment",item:"payment"));
        var posts=new List<SdkElement>{Post(type,"service"),Post(type,"payment","bank")};
        if(failure=="missing")posts.RemoveAt(1);
        if(failure=="ambiguous")posts.Add(Post(type,"payment","other-bank"));
        if(failure=="amount")posts[1]=posts[1] with {Children=posts[1].Children.Select(n=>n.Name=="Amount"?N("Amount","99.00"):n).ToArray()};
        var snapshot=new SalesReclassificationSnapshot(new HashSet<string>{"service"},posts,PaymentItemIds:failure=="unknown"?null:new HashSet<string>{"payment"});
        return SalesReclassificationPlanner.Build(new("bank","target",[type]),new Dictionary<string,IReadOnlyList<SdkElement>>{[type]=[form]},snapshot);
    }
    [Theory] [InlineData("Invoice")] [InlineData("SalesReceipt")] [InlineData("CreditMemo")]
    public void PaymentItemUsesHistoricalBankPostingWithoutSelectingServiceLines(string type)
    {
        var plan=PaymentPlan(type);Assert.Equal(new[]{"payment"},plan.Changes.Single().LineIds);
        var choices=plan.Plan.Requests.Single().Fields[type+"Mod"]!["$choices"]!;
        Assert.Null(choices[0]![type+"LineMod"]!["ClassRef"]);
        Assert.Equal("bank",choices[1]![type+"LineMod"]!["OverrideItemAccountRef"]!["ListID"]!.GetValue<string>());
        Assert.Equal("target",choices[1]![type+"LineMod"]!["ClassRef"]!["ListID"]!.GetValue<string>());
    }
    [Theory] [InlineData("ambiguous")] [InlineData("amount")] [InlineData("unknown")]
    public void UnverifiedPaymentPostingFailsBeforeMutation(string failure)=>Assert.Throws<QbException>(()=>PaymentPlan("Invoice",failure));
    private static ReclassificationPlan SpecialPlan(string type,string failure="")
    {
        var form=N(type+"Ret",null,N("TxnID","txn"),N("EditSequence","edit"),Line(type,"service"),Line(type,"subtotal",item:"subtotal"),Line(type,"discount",item:"discount"));
        SdkElement Discount(string amount)=>Post(type,"discount") with {Children=[N("TxnType",type),N("TxnID","txn"),N("TxnLineID","discount"),Ref("AccountRef","account"),N("Amount",amount)]};
        var posts=new List<SdkElement>{Post(type,"service"),Discount("-3.00")};
        if(failure=="subtotal-posting")posts.Add(Post(type,"subtotal","other-account"));
        if(failure=="discount-amount")posts[1]=Discount("-99.00");
        if(failure=="missing-discount")posts.RemoveAt(1);
        var snapshot=new SalesReclassificationSnapshot(new HashSet<string>{"service"},posts,
            DiscountItemIds:new HashSet<string>{"discount"},SubtotalItemIds:failure=="unknown-subtotal"?null:new HashSet<string>{"subtotal"});
        return SalesReclassificationPlanner.Build(new("account","target",[type]),new Dictionary<string,IReadOnlyList<SdkElement>>{[type]=[form]},snapshot);
    }
    [Theory] [InlineData("Invoice")] [InlineData("SalesReceipt")] [InlineData("CreditMemo")]
    public void SubtotalRetainsIdentityWithoutAccountOverrideAndDiscountCanSelectClass(string type)
    {
        var plan=SpecialPlan(type);var choices=plan.Plan.Requests.Single().Fields[type+"Mod"]!["$choices"]!;
        Assert.Equal(new[]{"service","discount"},plan.Changes.Single().LineIds);
        Assert.Null(choices[1]![type+"LineMod"]!["OverrideItemAccountRef"]);
        Assert.Null(choices[1]![type+"LineMod"]!["ClassRef"]);
        Assert.Equal("target",choices[2]![type+"LineMod"]!["ClassRef"]!["ListID"]!.GetValue<string>());
        Assert.Equal(0,plan.ItemLinesOutsideScope);
    }
    [Theory] [InlineData("subtotal-posting")] [InlineData("discount-amount")] [InlineData("missing-discount")] [InlineData("unknown-subtotal")]
    public void UnverifiedSpecialItemShapeFailsWholePlan(string failure)=>Assert.Throws<QbException>(()=>SpecialPlan("Invoice",failure));
    [Fact] public void SalesTypesAreExplicitAndDefaultSelectionStaysEightTypes()
    {
        var defaultSelection=ReclassificationPlanner.Selection(new() {["accountId"]="account",["classId"]="target"});
        Assert.Equal(8,defaultSelection.TransactionTypes.Count);
        Assert.DoesNotContain("Invoice",defaultSelection.TransactionTypes);
        var explicitSelection=ReclassificationPlanner.Selection(new() {["accountId"]="account",["classId"]="target",["transactionTypes"]=new JsonArray("Invoice")});
        Assert.Equal(new[]{"Invoice"},explicitSelection.TransactionTypes);
        Assert.Throws<QbException>(()=>ReclassificationPlanner.Build(explicitSelection,new Dictionary<string,IReadOnlyList<SdkElement>> {["Invoice"]=[Form("Invoice")]}));
    }

    private sealed class Executor(bool incompleteServices=false,bool incompleteInventory=false,bool invalidAccounts=false,string? itemFailure=null):ISdkExecutor
    {
        public bool MutationSubmitted;
        public List<SdkPlan> Queries=[];
        public SdkExecutionResult ExecuteSdk(SdkPlan plan,string company,string? journal)
        {
            MutationSubmitted|=plan.Requests.Any(c=>!c.Operation.EndsWith("Query",StringComparison.Ordinal));
            var results=plan.Requests.Select(c=>new SdkOutcome(c.RequestId,c.Operation,"succeeded",0,"Info",null,null,
                [N(c.Operation=="AccountQuery"?"AccountRet":"ClassRet",null,N("ListID",c.RequestId is "account" or "accounts"?"account":"target"),N("IsActive","true"),
                    N("AccountType",invalidAccounts && c.RequestId=="accounts"?null:"Income"))])).ToArray();
            return new(true,1,company,"",false,false,journal,results);
        }
        public SdkQueryResult QuerySdkAll(SdkPlan plan,string company,int pageSize)
        {
            Queries.Add(plan); var operation=plan.Requests.Single().Operation;
            IReadOnlyList<SdkElement> data=operation switch
            {"InvoiceQuery"=>[Form("Invoice")],"ItemServiceQuery"=>[N("ItemServiceRet",null,N("ListID","service"))],
                "ItemInventoryQuery"=>[],
                "ItemNonInventoryQuery"=>itemFailure=="duplicate-kind"?[N("ItemNonInventoryRet",null,N("ListID","service"))]:[],
                "ItemOtherChargeQuery"=>[],
                "ItemDiscountQuery"=>[],"ItemSubtotalQuery"=>[],"ItemPaymentQuery"=>[],
                "TransactionQuery"=>[Post("Invoice","flat"),Post("Invoice","child"),Post("Invoice","other","other-account")],_=>throw new Exception("Unexpected query")};
            return new(true,1,company,operation,"13.0",!(incompleteServices && operation=="ItemServiceQuery") && !(incompleteInventory && operation=="ItemInventoryQuery") && operation!=itemFailure,1,data);
        }
    }
    [Theory] [InlineData(true,false)] [InlineData(false,true)]
    public void IncompleteInventoryOrMissingAccountTypesBlockPlanningBeforeWrites(bool inventory,bool accounts)
    {
        var executor=new Executor(incompleteInventory:inventory,invalidAccounts:accounts);
        Assert.Throws<QbException>(()=>ReclassificationPlanner.Read(executor,new("account","target",["Invoice"]),@"C:\Test.QBW","13.0","US",25,50));
        Assert.False(executor.MutationSubmitted);
    }
    [Fact] public void LivePlanningRequiresCompleteServiceAndPostingSnapshotsWithoutWrites()
    {
        var executor=new Executor();
        var plan=ReclassificationPlanner.Read(executor,new("account","target",["Invoice"]),@"C:\Test.QBW","13.0","US",25,50);
        Assert.False(executor.MutationSubmitted); Assert.Single(plan.Plan.Requests);
        Assert.Equal(new[]{"InvoiceQuery","ItemServiceQuery","ItemInventoryQuery","ItemNonInventoryQuery","ItemOtherChargeQuery","ItemDiscountQuery","ItemSubtotalQuery","ItemPaymentQuery","TransactionQuery"},executor.Queries.Select(p=>p.Requests.Single().Operation));
        Assert.True(executor.Queries[0].Requests[0].Fields["IncludeLinkedTxns"]!.GetValue<bool>());
        Assert.Equal("All",executor.Queries[1].Requests[0].Fields["ActiveStatus"]!.GetValue<string>());
        Assert.Equal("AllExceptSummary",executor.Queries[8].Requests[0].Fields["TransactionDetailLevelFilter"]!.GetValue<string>());
        var incomplete=new Executor(true);
        Assert.Throws<QbException>(()=>ReclassificationPlanner.Read(incomplete,new("account","target",["Invoice"]),@"C:\Test.QBW","13.0","US",25,50));
        Assert.False(incomplete.MutationSubmitted);
    }
    private static SdkElement N(string name,string? value=null,params SdkElement[] children)=>new(name,new Dictionary<string,string>(),value,children);
    private static SdkElement Ref(string name,string id)=>N(name,null,N("ListID",id));
    private static SdkElement Line(string type,string id,string? cls=null,string item="service")=>N(type+"LineRet",null,[
        N("TxnLineID",id),Ref("ItemRef",item),N("Amount","3.00"),N("Desc","retained"),
        ..(cls is null?Array.Empty<SdkElement>():[Ref("ClassRef",cls)])]);
    private static SdkElement Form(string type)=>N(type+"Ret",null,N("TxnID","txn"),N("EditSequence","edit"),N("Memo","retained"),
        Line(type,"flat"),N(type+"LineGroupRet",null,N("TxnLineID","group"),N("Quantity","1"),Line(type,"child"),Line(type,"other","existing")));
    private static SdkElement Post(string type,string id,string account="account")=>N("TransactionRet",null,N("TxnType",type),N("TxnID","txn"),
        N("TxnLineID",id),Ref("AccountRef",account),N("Amount",type=="CreditMemo"?"-3.00":"3.00"),N("TimeModified","old"));
    private static ReclassificationPlan Plan(string type="Invoice",SdkElement? form=null,IReadOnlyList<SdkElement>? posts=null,IReadOnlySet<string>? services=null)=>
        SalesReclassificationPlanner.Build(new("account","target",[type]),new Dictionary<string,IReadOnlyList<SdkElement>>{[type]=[form??Form(type)]},
            new(services??new HashSet<string>{"service"},posts??[Post(type,"flat"),Post(type,"child"),Post(type,"other","other-account")]));

    [Theory] [InlineData("Invoice")] [InlineData("SalesReceipt")] [InlineData("CreditMemo")]
    public void RetainsOrderedGroupsAndActualAccountsWithOneModifierPerTransaction(string type)
    {
        var plan=Plan(type); var body=plan.Plan.Requests.Single().Fields[type+"Mod"]!;
        Assert.Equal(new[]{"flat","child"},plan.Changes.Single().LineIds);
        Assert.Equal("edit",body["EditSequence"]!.GetValue<string>());
        var choices=body["$choices"]!.AsArray(); Assert.Equal(2,choices.Count);
        Assert.Equal("account",choices[0]![type+"LineMod"]!["OverrideItemAccountRef"]!["ListID"]!.GetValue<string>());
        var group=choices[1]![type+"LineGroupMod"]!;
        Assert.Equal("group",group["TxnLineID"]!.GetValue<string>());
        Assert.Equal(2,group[type+"LineMod"]!.AsArray().Count);
        Assert.Equal("existing",group[type+"LineMod"]![1]!["ClassRef"]!["ListID"]!.GetValue<string>());
        Assert.Equal("other-account",group[type+"LineMod"]![1]!["OverrideItemAccountRef"]!["ListID"]!.GetValue<string>());
        Assert.Equal(3,plan.Changes.Single().OriginalPostings!.Count);
        Assert.DoesNotContain("ItemRef",body.ToJsonString()); Assert.DoesNotContain("Amount",body.ToJsonString());
    }

    [Fact] public void MissingPostingAccountIsNeverInferredFromItemDefaults()
    {
        Assert.Throws<QbException>(()=>Plan(posts:[Post("Invoice","flat"),Post("Invoice","other")]));
    }
    [Fact] public void AmbiguousSelectedPostingFailsBeforeAnyPlanCanApply()
    {
        Assert.Throws<QbException>(()=>Plan(posts:[Post("Invoice","flat"),Post("Invoice","flat","other-account"),Post("Invoice","child"),Post("Invoice","other")]));
    }
    [Fact] public void UnsupportedUnselectedItemInChangedTransactionFailsPreservationRecipe()
    {
        var form=Form("Invoice"); var group=form.Children.Last();
        form=form with {Children=[..form.Children.Take(form.Children.Count-1),group with {Children=[..group.Children.Take(group.Children.Count-1),Line("Invoice","other","existing","inventory")] }]};
        Assert.Throws<QbException>(()=>Plan(form:form));
    }
    [Fact] public void UnsupportedItemsWithoutAnySelectedAccountDoNotProduceChanges()
    {
        var plan=Plan(posts:[Post("Invoice","flat","other"),Post("Invoice","child","other"),Post("Invoice","other","other")],services:new HashSet<string>());
        Assert.Empty(plan.Plan.Requests); Assert.Equal(3,plan.ItemLinesOutsideScope);
    }
    [Fact] public void AmountMismatchCannotGenerateModifier()
    {
        var post=Post("Invoice","flat"); post=post with {Children=post.Children.Select(c=>c.Name=="Amount"?c with {Value="99.00"}:c).ToArray()};
        Assert.Throws<QbException>(()=>Plan(posts:[post,Post("Invoice","child"),Post("Invoice","other")]));
    }
    [Fact] public void DuplicateLeafIdentityFails()
    {
        var form=Form("Invoice"); form=form with {Children=[..form.Children,Line("Invoice","flat")]};
        Assert.Throws<QbException>(()=>Plan(form:form));
    }
    [Fact] public void ExistingClassesAreNotOverwritten()
    {
        var form=Form("Invoice"); form=form with {Children=form.Children.Select(c=>c.Name=="InvoiceLineRet"?Line("Invoice","flat","already"):c).ToArray()};
        var plan=Plan(form:form); Assert.Equal(new[]{"child"},plan.Changes.Single().LineIds); Assert.Equal(1,plan.AlreadyClassifiedLines);
    }
    [Fact] public void ReadBackDetectsUnselectedFieldLossAndChangedAccounts()
    {
        var plan=Plan(); var change=plan.Changes.Single();
        SdkElement Classified(SdkElement n)=>n with {Children=n.Children.Select(Classified).Concat(n.Name=="InvoiceLineRet" && n.Children.Any(c=>c.Name=="TxnLineID" && c.Value is "flat" or "child")?[Ref("ClassRef","target")]:Array.Empty<SdkElement>()).ToArray()};
        var current=Classified(change.Original);
        Assert.True(SalesReclassificationPlanner.Check(change,current,"target").ClassVerified);
        Assert.True(SalesReclassificationPlanner.Check(change,current,"target").OtherFieldsPreserved);
        Assert.False(SalesReclassificationPlanner.Check(change,current with {Children=current.Children.Where(c=>c.Name!="Memo").ToArray()},"target").OtherFieldsPreserved);
        Assert.True(SalesReclassificationPlanner.PostingsPreserved(change.OriginalPostings!,change.OriginalPostings!.Reverse().ToArray()));
        Assert.False(SalesReclassificationPlanner.PostingsPreserved(change.OriginalPostings!,[Post("Invoice","flat","changed"),Post("Invoice","child"),Post("Invoice","other","other-account")]));
    }

    private static ReclassificationPlan InventoryPlan(string type="Invoice",string selected="account",string failure="")
    {
        var form=Form(type);
        form=form with {Children=form.Children.Select(c=>c.Name==type+"LineRet"?Line(type,"flat",item:"inventory"):c).ToArray()};
        SdkElement AmountPost(string account,string amount)=>Post(type,"flat",account) with
            {Children=Post(type,"flat",account).Children.Select(c=>c.Name=="Amount"?c with {Value=amount}:c).ToArray()};
        var posts=new List<SdkElement>{AmountPost("asset","-2.00"),Post(type,"child"),AmountPost("cogs","2.00"),Post(type,"other","other-account"),Post(type,"flat")};
        var types=new Dictionary<string,string>{{"account","Income"},{"asset","OtherCurrentAsset"},{"cogs","CostOfGoodsSold"},{"other-account","Income"}};
        if(failure=="missing") posts.RemoveAt(0);
        if(failure=="extra") posts.Add(AmountPost("other-account","3.00"));
        if(failure=="unknown") types.Remove("asset");
        if(failure=="ambiguous") types["asset"]="Income";
        if(failure=="unbalanced") posts[0]=AmountPost("asset","-9.00");
        if(failure=="revenue-amount") posts[^1]=AmountPost("account","99.00");
        return SalesReclassificationPlanner.Build(new(selected,"target",[type]),new Dictionary<string,IReadOnlyList<SdkElement>>{[type]=[form]},
            new(new HashSet<string>{"service"},posts,new HashSet<string>{"inventory"},types));
    }
    [Theory] [InlineData("Invoice")] [InlineData("SalesReceipt")] [InlineData("CreditMemo")]
    public void InventoryRevenueIsResolvedByAccountTypeRegardlessOfPostingOrder(string type)
    {
        var plan=InventoryPlan(type);
        var body=plan.Plan.Requests.Single().Fields[type+"Mod"]!;
        Assert.Equal("account",body["$choices"]![0]![type+"LineMod"]!["OverrideItemAccountRef"]!["ListID"]!.GetValue<string>());
        Assert.Equal(new[]{"flat","child"},plan.Changes.Single().LineIds);
        Assert.Equal(5,plan.Changes.Single().OriginalPostings!.Count);
    }
    [Theory] [InlineData("missing")] [InlineData("extra")] [InlineData("unknown")]
    [InlineData("ambiguous")] [InlineData("unbalanced")] [InlineData("revenue-amount")]
    public void InventoryIncompleteOrAmbiguousPostingShapeIsRejected(string failure)=>Assert.Throws<QbException>(()=>InventoryPlan(failure:failure));

    [Theory] [InlineData("cogs")] [InlineData("asset")]
    public void InventoryCostOrAssetAccountSelectionIsRejected(string account)=>Assert.Throws<QbException>(()=>InventoryPlan(selected:account));

    [Theory] [InlineData("ItemNonInventoryQuery")] [InlineData("ItemOtherChargeQuery")] [InlineData("ItemDiscountQuery")] [InlineData("ItemSubtotalQuery")] [InlineData("ItemPaymentQuery")] [InlineData("duplicate-kind")]
    public void AdditionalItemQueriesMustBeCompleteAndKindsCannotOverlap(string failure)
    {
        var executor=new Executor(itemFailure:failure);
        Assert.Throws<QbException>(()=>ReclassificationPlanner.Read(executor,new("account","target",["Invoice"]),@"C:\Test.QBW","13.0","US",25,50));
        Assert.False(executor.MutationSubmitted);
    }
    [Theory] [InlineData("Invoice",false)] [InlineData("SalesReceipt",false)] [InlineData("CreditMemo",false)]
    [InlineData("Invoice",true)] [InlineData("SalesReceipt",true)] [InlineData("CreditMemo",true)]
    public void NonInventoryAndOtherChargeLeavesUseActualSinglePostingAccount(string type,bool otherCharge)
    {
        var snapshot=new SalesReclassificationSnapshot(new HashSet<string>(),[Post(type,"flat"),Post(type,"child"),Post(type,"other","other-account")],
            NonInventoryItemIds:otherCharge?null:new HashSet<string>{"service"},OtherChargeItemIds:otherCharge?new HashSet<string>{"service"}:null);
        var plan=SalesReclassificationPlanner.Build(new("account","target",[type]),new Dictionary<string,IReadOnlyList<SdkElement>>{[type]=[Form(type)]},snapshot);
        Assert.Equal(new[]{"flat","child"},plan.Changes.Single().LineIds);
        Assert.Equal("account",plan.Plan.Requests.Single().Fields[type+"Mod"]!["$choices"]![0]![type+"LineMod"]!["OverrideItemAccountRef"]!["ListID"]!.GetValue<string>());
    }
}
