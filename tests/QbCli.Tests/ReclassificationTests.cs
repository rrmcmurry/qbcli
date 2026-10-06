using System.Text.Json.Nodes;
using QbCli;
using QbCli.Core;
using QbCli.QuickBooks;
using Xunit;

namespace QbCli.Tests;

public class ReclassificationTests
{
    private static SdkElement E(string name, string? value = null, params SdkElement[] children) => new(name, new Dictionary<string,string>(), value, children);
    private static SdkElement Line(string id, string account, string? cls = null) => E("ExpenseLineRet", null,
        E("TxnLineID", id), E("AccountRef", null, E("ListID", account)), E("Amount", "12.34"),
        cls is null ? E("Memo", "original") : E("ClassRef", null, E("ListID", cls)));
    private static SdkElement Record(params SdkElement[] lines) => E("CheckRet", null,
        [E("TxnID", "txn"), E("EditSequence", "edit"), E("TimeModified", "2026-10-05T12:00:00"), E("Amount", "37.02"), ..lines]);
    private static readonly ReclassificationSelection Selection = new("account", "rental", ["Check"]);
    [Fact] public void DepositPlanPreservesDirectAndLinkedPaymentBranches()
    {
        var direct=Line("1","account") with {Name="DepositLineRet"};
        var linked=Line("2","account") with {Name="DepositLineRet",Children=[..Line("2","account").Children,E("TxnID","payment"),E("PaymentTxnLineID","payment-line")]};
        var keep=Line("3","other","existing") with {Name="DepositLineRet",Children=[..Line("3","other","existing").Children,E("TxnID","keep-payment")]};
        var original=Record(direct,linked,keep) with {Name="DepositRet"};
        var selection=Selection with {TransactionTypes=["Deposit"]};
        var plan=ReclassificationPlanner.Build(selection,new Dictionary<string,IReadOnlyList<SdkElement>>{{"Deposit",[original]}});
        var call=Assert.Single(plan.Plan.Requests);
        Assert.Equal("DepositMod",call.Operation);
        var lines=call.Fields["DepositMod"]!["DepositLineMod"]!.AsArray();
        Assert.Equal(3,lines.Count);
        Assert.Equal("account",lines[0]!["AccountRef"]!["ListID"]!.GetValue<string>());
        Assert.Equal("rental",lines[0]!["ClassRef"]!["ListID"]!.GetValue<string>());
        Assert.Null(lines[1]!["PaymentTxnID"]);Assert.Null(lines[1]!["PaymentTxnLineID"]);
        Assert.Equal("rental",lines[1]!["ClassRef"]!["ListID"]!.GetValue<string>());
        Assert.Equal("account",lines[1]!["AccountRef"]!["ListID"]!.GetValue<string>());
        Assert.Equal("existing",lines[2]!["ClassRef"]!["ListID"]!.GetValue<string>());
        Assert.Equal(["1","2"],Assert.Single(plan.Changes).LineIds);
        var current=original with {Children=original.Children.Select(c=>c.Name=="DepositLineRet" && plan.Changes[0].LineIds.Contains(c.Children[0].Value!)
            ? c with {Children=[..c.Children,E("ClassRef",null,E("ListID","rental"))]} : c).ToArray()};
        var check=ReclassificationPlanner.Check(plan.Changes[0],current,"rental");
        Assert.True(check.ClassVerified);Assert.True(check.OtherFieldsPreserved);
        var unlinked=current with {Children=current.Children.Select(c=>c.Name=="DepositLineRet" ? c with {Children=c.Children.Where(f=>f.Name!="TxnID").ToArray()} : c).ToArray()};
        Assert.False(ReclassificationPlanner.Check(plan.Changes[0],unlinked,"rental").OtherFieldsPreserved);
    }
    [Fact] public void DepositPaymentLineWithoutPaymentIdentityAborts()
    {
        var line=Line("1","account") with {Name="DepositLineRet",Children=[..Line("1","account").Children,E("PaymentTxnLineID","orphan")]};
        Assert.Throws<QbException>(()=>ReclassificationPlanner.Build(Selection with {TransactionTypes=["Deposit"]},
            new Dictionary<string,IReadOnlyList<SdkElement>>{{"Deposit",[Record(line) with {Name="DepositRet"}]}}));
    }
    [Fact] public void JournalPlanCombinesDebitAndCreditChangesAndRetainsAllLines()
    {
        var original=Record(Line("1","account") with {Name="JournalDebitLine"},
            Line("2","account","existing") with {Name="JournalDebitLine"},
            Line("3","other") with {Name="JournalCreditLine"},
            Line("4","account") with {Name="JournalCreditLine"}) with {Name="JournalEntryRet"};
        var selection=Selection with {TransactionTypes=["JournalEntry"]};
        var planned=ReclassificationPlanner.Build(selection,new Dictionary<string,IReadOnlyList<SdkElement>>{{"JournalEntry",[original]}});
        var call=Assert.Single(planned.Plan.Requests);
        Assert.Equal("JournalEntryMod",call.Operation);
        var lines=call.Fields["JournalEntryMod"]!["JournalLineMod"]!.AsArray();
        Assert.Equal(4,lines.Count);
        Assert.Equal(["1","4"],Assert.Single(planned.Changes).LineIds);
        Assert.Equal("existing",lines[1]!["ClassRef"]!["ListID"]!.GetValue<string>());
        Assert.Null(lines[2]!["ClassRef"]);
        Assert.All(lines,l=>Assert.Null(l!["Amount"]));
        var current=original with {Children=original.Children.Select(c=>c.Name is "JournalDebitLine" or "JournalCreditLine" && planned.Changes[0].LineIds.Contains(c.Children[0].Value!)
            ? c with {Children=[..c.Children,E("ClassRef",null,E("ListID","rental"))]} : c).ToArray()};
        var verified=ReclassificationPlanner.Check(planned.Changes[0],current,"rental");
        Assert.True(verified.ClassVerified);Assert.True(verified.OtherFieldsPreserved);
        var missing=current with {Children=current.Children.Where(c=>c!=current.Children.Last()).ToArray()};
        Assert.False(ReclassificationPlanner.Check(planned.Changes[0],missing,"rental").OtherFieldsPreserved);
        var changedSide=current with {Children=current.Children.Select(c=>c.Name=="JournalCreditLine" ? c with {Name="JournalDebitLine"} : c).ToArray()};
        Assert.False(ReclassificationPlanner.Check(planned.Changes[0],changedSide,"rental").OtherFieldsPreserved);
    }
    [Fact] public void MissingPostingAccountIdentityAbortsRatherThanTreatingItAsAnotherAccount()
    {
        var missing=Line("1","account") with {Children=[E("TxnLineID","1"),E("AccountRef",null,E("FullName","Unresolved"))]};
        Assert.Throws<QbException>(()=>ReclassificationPlanner.Build(Selection,new Dictionary<string,IReadOnlyList<SdkElement>>{{"Check",[Record(missing)]}}));
    }
    [Fact] public void CombinesEligibleLinesAndPreservesClassifiedAndOtherAccounts()
    {
        var original = Record(Line("1", "account"), Line("2", "account", "existing"), Line("3", "other"), Line("4", "account"));
        var result = ReclassificationPlanner.Build(Selection, new Dictionary<string,IReadOnlyList<SdkElement>> { ["Check"] = [original] });
        Assert.Single(result.Plan.Requests); Assert.Equal(1, result.AlreadyClassifiedLines); Assert.Equal(1, result.OtherAccountLines);
        Assert.Equal(["1", "4"], result.Changes.Single().LineIds);
        var body = result.Plan.Requests.Single().Fields["CheckMod"]!.AsObject();
        Assert.Equal("edit", body["EditSequence"]!.GetValue<string>()); Assert.Equal(4, body["ExpenseLineMod"]!.AsArray().Count);
        Assert.Equal("existing", body["ExpenseLineMod"]![1]!["ClassRef"]!["ListID"]!.GetValue<string>());
        Assert.DoesNotContain("ClearExpenseLines", body.Select(p => p.Key));
        Assert.DoesNotContain("Amount", body["ExpenseLineMod"]![0]!.AsObject().Select(p => p.Key));
    }
    [Fact] public void VerificationDetectsDeletedUntouchedLinesAndChangedAmounts()
    {
        var original = Record(Line("1", "account"), Line("2", "other"));
        var change = new ReclassificationChange("Check", "txn", "edit", ["1"], original);
        var changedLine = Line("1", "account") with { Children = [..Line("1", "account").Children, E("ClassRef", null, E("ListID", "rental"))] };
        var correct = Record(changedLine, Line("2", "other"));
        Assert.True(ReclassificationPlanner.Check(change, correct, "rental").OtherFieldsPreserved);
        Assert.True(ReclassificationPlanner.Check(change, correct, "rental").ClassVerified);
        Assert.False(ReclassificationPlanner.Check(change, Record(changedLine), "rental").OtherFieldsPreserved);
        var damaged = correct with { Children = correct.Children.Select(c => c.Name == "Amount" ? c with { Value = "999" } : c).ToArray() };
        Assert.False(ReclassificationPlanner.Check(change, damaged, "rental").OtherFieldsPreserved);
        Assert.False(ReclassificationPlanner.Check(change, null, "rental").ClassVerified);
    }
    [Fact] public void DuplicateLineIdsAndIncompleteRecordsAbortPlanning()
    {
        var records = new Dictionary<string,IReadOnlyList<SdkElement>> { ["Check"] = [Record(Line("1", "account"), Line("1", "other"))] };
        Assert.Throws<QbException>(() => ReclassificationPlanner.Build(Selection, records));
        records["Check"] = [Record(Line("0", "account"))];
        Assert.Throws<QbException>(() => ReclassificationPlanner.Build(Selection, records));
    }
    [Theory]
    [InlineData("{\"accountId\":\"a\",\"classId\":\"b\",\"transactionTypes\":[\"SalesOrder\"]}")]
    [InlineData("{\"accountId\":\"a\",\"classId\":\"b\",\"transactionTypes\":[\"Check\",\"Check\"]}")]
    [InlineData("{\"accountId\":\"a\",\"classId\":\"b\",\"transactionTypes\":[]}")]
    [InlineData("{\"accountId\":\"a\",\"classId\":\"b\",\"extra\":true}")]
    public void RejectsAmbiguousSelections(string input) => Assert.Throws<QbException>(() => ReclassificationPlanner.Selection(JsonNode.Parse(input)!.AsObject()));
    [Fact] public void BulkCommandRequiresExplicitCompanyEvenForPreview()
    {
        Assert.Throws<QbException>(() => Options.Parse(["bulk", "reclassify", "--input", "selection.json"]));
        var parsed = Options.Parse(["bulk", "reclassify", "--input", "selection.json", "--company-file", "C:\\test\\Test.QBW", "--page-size", "1", "--apply"]);
        Assert.True(parsed.Apply); Assert.Equal(1, parsed.PageSize);
    }
}
