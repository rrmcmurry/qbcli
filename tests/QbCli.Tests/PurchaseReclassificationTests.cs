using System.Text.Json.Nodes;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class PurchaseReclassificationTests
{
    private static ReclassificationPlan InventoryPlan(string type,string failure="",string account="asset")
    {
        var form=N(type+"Ret",null,N("TxnID","txn"),N("EditSequence","edit"),Item("flat","inventory"));
        var posts=new List<SdkElement>{Post(type,"flat","asset")};
        if(type is "VendorCredit" or "CreditCardCredit") posts.AddRange([Post(type,"flat","asset","-1.00"),Post(type,"flat","cogs","1.00")]);
        if(failure=="unbalanced")posts[^1]=Post(type,"flat","cogs","2.00");
        if(failure=="ambiguous")posts.Add(Post(type,"flat","asset","-3.00"));
        if(failure=="extra")posts.Add(Post(type,"flat","other","1.00"));
        if(failure=="amount")posts[0]=Post(type,"flat","asset","99.00");
        if(failure=="wrong-adjustment")posts[1]=Post(type,"flat","other","-1.00");
        var kinds=new Dictionary<string,string>{{"asset","OtherCurrentAsset"},{"cogs","CostOfGoodsSold"},{"other","OtherCurrentAsset"}};
        if(failure=="unknown-account")kinds.Clear();
        var snapshot=new SalesReclassificationSnapshot(new HashSet<string>(),posts,new HashSet<string>{"inventory"},kinds);
        return PurchaseItemReclassificationPlanner.Build(new(account,"target",[type]),new Dictionary<string,IReadOnlyList<SdkElement>>{[type]=[form]},snapshot,"13.0","US",3);
    }
    [Theory] [InlineData("Check")] [InlineData("Bill")] [InlineData("CreditCardCharge")]
    [InlineData("CreditCardCredit")] [InlineData("VendorCredit")] [InlineData("ItemReceipt")]
    public void InventorySelectsActualAssetPostingAndPreservesCompletePostingEvidence(string type)
    {
        var plan=InventoryPlan(type);
        Assert.Equal("asset",plan.Plan.Requests.Single().Fields[type+"Mod"]!["$choices"]![0]!["ItemLineMod"]!["OverrideItemAccountRef"]!["ListID"]!.GetValue<string>());
        Assert.Equal(type is "VendorCredit" or "CreditCardCredit"?3:1,plan.Changes.Single().OriginalPostings!.Count);
    }
    [Theory] [InlineData("unbalanced")] [InlineData("ambiguous")] [InlineData("extra")]
    [InlineData("amount")] [InlineData("wrong-adjustment")] [InlineData("unknown-account")]
    public void UnverifiedInventoryPostingShapeStopsBeforeMutation(string failure)=>Assert.Throws<QbException>(()=>InventoryPlan("VendorCredit",failure));
    [Fact] public void InventoryCostAdjustmentAccountCannotSelectItemClass()=>Assert.Empty(InventoryPlan("VendorCredit",account:"cogs").Changes);
    private static SdkElement N(string name,string? value=null,params SdkElement[] children)=>new(name,new Dictionary<string,string>(),value,children);
    private static SdkElement Ref(string name,string id)=>N(name,null,N("ListID",id));
    private static SdkElement Item(string id,string item="service",string? cls=null)=>N("ItemLineRet",null,[N("TxnLineID",id),Ref("ItemRef",item),N("Amount","3.00"),
        ..(cls is null?Array.Empty<SdkElement>():[Ref("ClassRef",cls)])]);
    private static SdkElement Form(string type)=>N(type+"Ret",null,N("TxnID","txn"),N("EditSequence","edit"),N("Memo","preserved"),
        N("ExpenseLineRet",null,N("TxnLineID","expense"),Ref("AccountRef","account"),N("Amount","5.00")),
        N("ExpenseLineRet",null,N("TxnLineID","other-expense"),Ref("AccountRef","other"),N("Amount","6.00"),Ref("ClassRef","existing")),
        Item("flat"),N("ItemGroupLineRet",null,N("TxnLineID","group"),N("Quantity","1"),Item("child","noninventory"),Item("other","charge","existing")));
    private static SdkElement Post(string type,string line,string account="account",string amount="3.00")=>N("TransactionRet",null,N("TxnType",type),N("TxnID","txn"),N("TxnLineID",line),Ref("AccountRef",account),N("Amount",amount));
    private static ReclassificationPlan Plan(string type="Bill",string failure="")
    {
        var form=Form(type);var posts=new List<SdkElement>{Post(type,"flat"),Post(type,"child"),Post(type,"other","other"),Post(type,"expense",amount:"5.00")};
        if(failure=="missing")posts.RemoveAt(1);
        if(failure=="ambiguous")posts.Add(Post(type,"flat","other"));
        if(failure=="amount")posts[0]=Post(type,"flat",amount:"99.00");
        var service=new HashSet<string>{"service"};var noninventory=new HashSet<string>{"noninventory"};var charge=new HashSet<string>{"charge"};
        if(failure=="unknown-retained")charge.Clear();
        if(failure=="duplicate")form=form with {Children=[..form.Children,Item("flat")]};
        var snapshot=new SalesReclassificationSnapshot(service,posts,NonInventoryItemIds:noninventory,OtherChargeItemIds:charge);
        return ReclassificationPlanner.Build(new("account","target",[type]),new Dictionary<string,IReadOnlyList<SdkElement>>{[type]=[form]},sales:snapshot);
    }
    [Theory] [InlineData("Check")] [InlineData("Bill")] [InlineData("CreditCardCharge")]
    [InlineData("CreditCardCredit")] [InlineData("VendorCredit")] [InlineData("ItemReceipt")]
    public void ItemAndExpenseChangesUseOneModifierAndRetainEveryBranch(string type)
    {
        var plan=Plan(type);var body=plan.Plan.Requests.Single().Fields[type+"Mod"]!;
        Assert.Equal(new[]{"expense","flat","child"},plan.Changes.Single().LineIds);
        Assert.Equal(2,body["ExpenseLineMod"]!.AsArray().Count);
        Assert.Equal("existing",body["ExpenseLineMod"]![1]!["ClassRef"]!["ListID"]!.GetValue<string>());
        Assert.Equal(2,body["$choices"]!.AsArray().Count);
        Assert.Equal("account",body["$choices"]![0]!["ItemLineMod"]!["OverrideItemAccountRef"]!["ListID"]!.GetValue<string>());
        var children=body["$choices"]![1]!["ItemGroupLineMod"]!["ItemLineMod"]!;
        Assert.Equal(2,children.AsArray().Count);Assert.Equal("other",children[1]!["OverrideItemAccountRef"]!["ListID"]!.GetValue<string>());
        Assert.Equal(1,plan.ScannedTransactions);Assert.Equal(0,plan.ItemLinesOutsideScope);
        Assert.NotNull(plan.Changes.Single().OriginalPostings);
    }
    [Theory] [InlineData("missing")] [InlineData("ambiguous")] [InlineData("amount")]
    [InlineData("unknown-retained")] [InlineData("duplicate")]
    public void UnsupportedRetainedItemOrInvalidPostingStopsWholeCombinedPlan(string failure)=>Assert.Throws<QbException>(()=>Plan(failure:failure));
    [Fact] public void CombinedReadBackChecksExpenseAndGroupClassesAndAllOtherFields()
    {
        var change=Plan().Changes.Single();
        SdkElement Classified(SdkElement n)=>n with {Children=n.Children.Select(Classified).Concat(n.Name is "ExpenseLineRet" or "ItemLineRet"
            && n.Children.Any(c=>c.Name=="TxnLineID" && change.LineIds.Contains(c.Value))?[Ref("ClassRef","target")]:Array.Empty<SdkElement>()).ToArray()};
        var current=Classified(change.Original);
        Assert.True(ReclassificationPlanner.Check(change,current,"target").ClassVerified);
        Assert.True(ReclassificationPlanner.Check(change,current,"target").OtherFieldsPreserved);
        Assert.False(ReclassificationPlanner.Check(change,current with {Children=current.Children.Where(c=>c.Name!="Memo").ToArray()},"target").OtherFieldsPreserved);
        Assert.False(ReclassificationPlanner.Check(change,change.Original,"target").ClassVerified);
    }
    [Fact] public void ItemOnlyPlanOmitsExpenseModifiers()
    {
        var form=Form("Bill");form=form with {Children=form.Children.Where(c=>c.Name!="ExpenseLineRet").ToArray()};
        var snapshot=new SalesReclassificationSnapshot(new HashSet<string>{"service","noninventory","charge"},[Post("Bill","flat"),Post("Bill","child"),Post("Bill","other","other")]);
        var plan=ReclassificationPlanner.Build(new("account","target",["Bill"]),new Dictionary<string,IReadOnlyList<SdkElement>>{["Bill"]=[form]},sales:snapshot);
        Assert.Null(plan.Plan.Requests.Single().Fields["BillMod"]!["ExpenseLineMod"]);
    }
    private sealed class EmptyExecutor:ISdkExecutor
    {
        public List<SdkPlan> Queries=[];
        public SdkExecutionResult ExecuteSdk(SdkPlan plan,string company,string? journal)
        {
            SdkBatch.Prepare(plan);
            return new(true,1,company,"",false,false,journal,plan.Requests.Select(c=>new SdkOutcome(c.RequestId,c.Operation,"succeeded",0,"Info",null,null,
                [N(c.Operation=="ClassQuery"?"ClassRet":"AccountRet",null,N("ListID",c.Operation=="ClassQuery"?"target":"account"),N("IsActive","true"),N("AccountType","Expense"))])).ToArray());
        }
        public SdkQueryResult QuerySdkAll(SdkPlan plan,string company,int pageSize)
        {
            SdkBatch.Prepare(plan);Queries.Add(plan);return new(true,1,company,plan.Requests[0].Operation,"13.0",true,1,[]);
        }
    }
    [Theory] [InlineData("Check",true)] [InlineData("Bill",true)] [InlineData("VendorCredit",true)] [InlineData("ItemReceipt",true)]
    [InlineData("CreditCardCharge",false)] [InlineData("CreditCardCredit",false)]
    public void PurchaseSourceQueriesRequestLinkedTransactionsOnlyWhereSupported(string type,bool linked)
    {
        var executor=new EmptyExecutor();
        var plan=ReclassificationPlanner.Read(executor,new("account","target",[type]),@"C:\Test.QBW","13.0","US",25,50);
        Assert.Empty(plan.Plan.Requests);
        Assert.Equal(linked,executor.Queries[0].Requests[0].Fields.ContainsKey("IncludeLinkedTxns"));
        if(linked)Assert.True(executor.Queries[0].Requests[0].Fields["IncludeLinkedTxns"]!.GetValue<bool>());
    }
}
