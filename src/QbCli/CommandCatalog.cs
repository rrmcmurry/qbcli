using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QbCli.Core;
using QbCli.QuickBooks;

namespace QbCli;

public static class CommandCatalog
{
    private static string Resource(string suffix)
    {
        var assembly = typeof(CommandCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal)))!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static JsonObject Contract() => JsonNode.Parse(Resource("commands.json"))!.AsObject();
    public static IReadOnlyList<string> CommandNames => Contract()["commands"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToArray();
    public static JsonObject Discover(ConnectionInfo? connection = null,CompanyFeatureSnapshot? features = null)
    {
        var result = Contract();
        result["success"] = true;
        result["schemaVersion"] = 1;
        result["connection"] = connection is null ? new JsonObject { ["state"] = "not_connected" }
            : JsonSerializerNode(connection);
        result["companyFeatures"] = JsonNode.Parse(Output.Json(features??CompanyFeatureDiscovery.NotQueried()));
        result["sdkConnectionContract"] = new JsonObject
        {
            ["requestedFileMode"] = "--file-mode any|single-user|multi-user configures BeginSession. Default any accepts the current mode; the SDK may open an unopened company in single-user mode. A conflicting already-open mode can reject the session; this option cannot switch that company to another mode.",
            ["observedFileMode"] = "connection.host.fileMode is the separate HostQuery QBFileMode observation; requestedFileMode is session intent, not proof of the actual mode.",
            ["scope"] = "Company sessions only, including live discovery, convenience reads, SDK run/batch/resume and bulk reclassification. Offline mutation previews report the request without connecting. Subscription and event-listener transports reject this option.",
            ["authorization"] = "The same stable full SDK access profile is requested for every file mode. File mode does not grant permissions, enable features or bypass administrator authorization.",
            ["documentationUrl"] = "https://static.developer.intuit.com/resources/QBSDK_ProGuide.pdf"
        };
        result["companyFeatureContract"] = new JsonObject
        {
            ["source"]="PreferencesQuery, read only in the same STA company session after host/company discovery. Uses the highest advertised protocol within the imported reference supporting this operation. No preferences are changed.",
            ["interpretation"]="These are configuration observations, not SDK operation availability or write permission guarantees. An enabled flag does not prove a request will succeed; disabled class tracking did not prevent class-reference writes in the test company. Missing fields/groups and malformed/duplicate values remain unknown. sdkCatalog.availableInConnectedCompany remains null.",
            ["failure"]="The optional preferences probe may fail while host/company discovery remains useful; companyFeatures reports its sanitized error and all facts unknown. A company-identity mismatch fails the command.",
            ["privacy"]="Only selected boolean configuration flags and the documented serial/lot enum are retained. Usernames, preference account identities, rates, dates and unrelated values are omitted.",
            ["documentationUrl"]="https://developer.intuit.com/app/developer/qbdesktop/docs/api-reference/qbdesktop/preferencesquery"
        };
        var operations = new JsonArray();
        var coverage = JsonNode.Parse(Resource("live-coverage.json"))!.AsObject();
        var coverageIndex=coverage["requests"]!.AsArray().OfType<JsonObject>().GroupBy(r=>r["operation"]!.GetValue<string>(),StringComparer.Ordinal)
            .ToDictionary(g=>g.Key,g=>g.Cast<JsonNode>().ToArray(),StringComparer.Ordinal);
        foreach (var line in Resource("sdk-operations.txt").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Regex.Match(line, @"^([A-Za-z0-9]+)(?:\((\d+\.\d+)\))?$");
            if (!match.Success) throw new InvalidOperationException("Invalid SDK catalog entry.");
            string name = match.Groups[1].Value;
            var evidence=coverageIndex.GetValueOrDefault(name)??[];
            var commands = result["commands"]!.AsArray().Where(c => c!["sdkOperations"]!.AsArray().Any(o => o!.GetValue<string>() == name)).Select(c => c!["name"]!.GetValue<string>()).ToArray();
            operations.Add(new JsonObject
            {
                ["name"] = name,
                ["transportRole"] = name == "QBXMLEvents" ? "inbound_callback" : "outbound_request",
                ["documentationUrl"] = "https://developer.intuit.com/app/developer/qbdesktop/docs/api-reference/qbdesktop/" + name.ToLowerInvariant(),
                ["sidebarVersionAnnotation"] = match.Groups[2].Success ? match.Groups[2].Value : null,
                ["implementationStatus"] = commands.Length > 0 ? "implemented" : SdkReference.Operations.TryGetValue(name, out var definition) && definition.DesktopAvailable ? "schema_validated_interface" : "not_implemented",
                ["sdkCommand"] = SdkReference.Operations.TryGetValue(name, out var available) && available.DesktopAvailable ? "sdk " + (available.Transport == "subscription" ? "subscription " : "run ") + name : null,
                ["liveVerification"] = new JsonArray(evidence.Select(r => r.DeepClone()).ToArray()),
                ["liveVerificationSummary"]=SdkCoverageSummary.Operation(evidence),
                ["commands"] = new JsonArray(commands.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
                ["availableInConnectedCompany"] = null
            });
        }
        result["sdkCatalog"] = new JsonObject { ["source"] = "Intuit API reference navigation, retrieved 2026-10-05", ["scope"] = "Published operation names and documentation links; not a field-level schema, runtime authorization probe, or guarantee of availability. Missing version annotation means unknown. Includes integration and UI operations.", ["operationCount"] = operations.Count, ["operations"] = operations };
        result["sdkReference"] = SdkReference.Discover();
        result["sdkNumericContract"]=SdkNumericTypes.Discover();
        result["liveTestCoverage"] = coverage;
        result["liveCoverageSummary"]=new JsonObject
        {
            ["tested"]=coverage["tested"]!.DeepClone(),["host"]=coverage["host"]!.DeepClone(),["country"]=coverage["country"]!.DeepClone(),
            ["fixtureOutcomeCount"]=coverage["requests"]!.AsArray().Count,
            ["catalogOperationCount"]=operations.Count,
            ["requestOperationCoverage"]=SdkCoverageSummary.Catalog(operations.OfType<JsonNode>()),
            ["operationStateCounts"]=new JsonObject(operations.OfType<JsonObject>().GroupBy(o=>o["liveVerificationSummary"]!["state"]!.GetValue<string>(),StringComparer.Ordinal)
                .OrderBy(g=>g.Key,StringComparer.Ordinal).Select(g=>new KeyValuePair<string,JsonNode?>(g.Key,JsonValue.Create(g.Count())))),
            ["interpretation"]="Historical retained fixture outcomes on the named test host/country only. success_recorded includes at least one SDK succeeded outcome; no_match_only has accepted empty queries but no succeeded outcome; attempted_without_acceptance has only other outcomes; not_attempted has no retained request outcome. Counts are operation names, not every field, enum, lifecycle or SDK version. Fixture outcome counts may include repeated probes and are not independent session counts. Read behavioral checks for scoped preservation evidence. No current-company availability or permissions are inferred."
        };
        result["currentReferenceAudit"] = JsonNode.Parse(Resource("current-reference-audit.json"));
        result["currentEnumAudit"] = JsonNode.Parse(Resource("sdk-enum-audit.json"));
        result["currentConstraintAudit"] = JsonNode.Parse(Resource("sdk-constraint-audit.json"));
        result["currentFieldTableAudit"] = JsonNode.Parse(Resource("sdk-field-table-audit.json"));
        result["sdkEffectsContract"] = new JsonObject
        {
            ["meaning"]="Effect flags describe categories the prepared plan can affect, not proof that changes occurred. Use correlated per-request outcomes and fresh read-back to establish actual results. Mixed company plans can affect multiple categories.",
            ["accounting"]="mutatesCompany identifies accounting/list mutations through company SDK requests. It excludes event recovery-state deletion, subscription transport, and form-display operations.",
            ["recovery"]="mutatesRecoveryState covers DataEventRecoveryInfoDel and explicit newMessageSetID/oldMessageSetID controls. Deleting an absent event recovery record returned3120 and remained a failure in the tested company; it is not normalized to query no_match or successful deletion.",
            ["ui"]="affectsUserInterface covers ListDisplayAdd/Mod and TxnDisplayAdd/Mod. These display QuickBooks forms; they are not accounting writes submitted by qbcli. Human edits in a displayed form are outside this flag's meaning. Live form-display behavior remains unverified.",
            ["subscription"]="mutatesSubscriptions identifies installation-scoped subscription changes. It is separate from company-scoped accounting/recovery effects.",
            ["guard"]="All non-query operations still require --apply and a new durable journal. Company transport also requires an explicit company path. Effect categorization does not reduce authorization or write-intent guards. Queries with explicit message-set recovery IDs require these controls too."
        };
        result["macroContract"] = new JsonObject
        {
            ["syntax"]="Only schema-documented macro attributes are accepted. Aggregate: $attributes:{defMacro:'TxnID:invoice_1'}. Scalar reference: {$value:'',$attributes:{useMacro:'TxnID:invoice_1'}}. Tag:name format; name is 1..40 ASCII letters/digits/dashes/underscores. A definition assigns one tag/name pair.",
            ["validation"]="Definitions must be unique across the plan. Every reference must exactly match a definition in an earlier request of this same plan. Undefined, forward and same-request references, plus duplicate definitions, are rejected before connecting. A definition may be referenced more than once. Standalone sdk run/validate cannot consume macros from another invocation. Structural validation does not guarantee SDK acceptance of a tag for every defining element.",
            ["session"]="One company session is reused for the entire batch plan. Macros survive count/byte splitting into multiple message sets within that session, then expire at EndSession. They cannot be persisted or consumed by a later CLI invocation.",
            ["failure"]="Macros do not make batches atomic. A failed defining request cannot supply its ID; continueOnError may report downstream reference failures in the same message set. Execution stops before later message sets after errors/warnings/unknown outcomes. sdk resume starts a new session and permits only self-contained pending macro chains: all definitions and uses must be in the proven-unexecuted remainder. References to completed/failed definitions need confirmed actual IDs in a separate plan. Uncertain submissions always block apply.",
            ["liveEvidence"]="InvoiceAdd -> ReceivePaymentAdd -> DepositAdd verified in one message set and three separate message sets in one session on Test.QBW; fresh queries verified paid invoice, applied payment, deposited payment link and amounts. Forty-character macro names also verified. DepositAdd/CashBackInfoAdd TxnID/TxnLineID macros followed by ClearedStatusMod passed both message layouts; independent ledger reads verified statuses and amounts, then cash-back status changed without changing the main deposit status. Other defining-element/tag combinations are not exhaustively tested.",
            ["documentationUrl"]="https://static.developer.intuit.com/resources/QBSDK_ProGuide.pdf"
        };
        result["creditDiscountApplicationContract"]=new JsonObject
        {
            ["selection"]="Use fresh company-scoped customer/vendor, AR/AP, invoice/bill, credit, discount account and class IDs. Supply AppliedToTxnAdd explicitly. Do not combine this recipe with automatic allocation or infer credit availability from original credit totals.",
            ["amounts"]="PaymentAmount is the requested cash application; SetCredit.AppliedAmount and DiscountAmount reduce the document separately. ReceivePayment.TotalAmount is cash, not cash plus credits/discounts. One vendor credit can be split across multiple AppliedToTxnAdd entries in one bill payment. Always inspect returned applications and fresh remaining balances; overpayment/unused cash may differ from submitted cash allocations.",
            ["verification"]="Fresh ReceivePaymentQuery/BillPaymentCheckQuery IncludeLineItems=true confirm cash applications, DiscountAmount, DiscountAccountRef, DiscountClassRef and credit links. Fresh InvoiceQuery/CreditMemoQuery/BillQuery/VendorCreditQuery IncludeLinkedTxns=true and BillToPayQuery confirm links, credit consumption and remaining amounts. Applied-credit modification/unapply remains outside this live proof; see creditOnlyApplicationContract for separate credit-only evidence.",
            ["liveScope"]="Test.QBW QBD2024R21 US protocol13: invoice100 received cash25/credit20/discount5 -> remaining50. Bills100/80 shared one vendor credit20 split10/10 plus cash25/25 and discounts5/5 -> remaining60/40. Subsequent cash completion yielded paid documents absent from open lists. Three discount classes/accounts, six normalized document states and journals passed independent retained evidence verification. See creditDiscountPaymentChecks. No multicurrency, overpayment or applied-credit modification proof. Separate credit-only evidence is described by creditOnlyApplicationContract.",
            ["exampleWarning"]="Structural example only. Replace every placeholder with confirmed company-scoped IDs and verify balances/available credits before apply. This example does not authorize payments in any company.",
            ["exampleFields"]=JsonNode.Parse("""
                {"ReceivePaymentAdd":{"CustomerRef":{"ListID":"customer-id"},"ARAccountRef":{"ListID":"ar-account-id"},
                "DepositToAccountRef":{"ListID":"bank-account-id"},"TotalAmount":"25.00",
                "AppliedToTxnAdd":{"TxnID":"invoice-id","PaymentAmount":"25.00","SetCredit":{"CreditTxnID":"credit-memo-id","AppliedAmount":"20.00"},
                "DiscountAmount":"5.00","DiscountAccountRef":{"ListID":"discount-account-id"},"DiscountClassRef":{"ListID":"discount-class-id"}}}}
                """),
            ["documentationUrl"]="https://static.developer.intuit.com/resources/QBSDK_ProGuide.pdf"
        };
        result["creditOnlyApplicationContract"]=new JsonObject
        {
            ["requests"]="Use explicit fresh document and credit IDs in AppliedToTxnAdd.SetCredit; omit PaymentAmount and DiscountAmount for credit-only applications. ReceivePaymentAdd.TotalAmount is zero. Mutation apply/company/journal requirements still apply.",
            ["response"]="SDK status0 can return a lean AppliedToTxnRet containing only existing document identity, without a new payment ID or amount. Preserve that successful response; never invent a created ID, treat omitted numeric fields as zero, or automatically retry because an ID is absent.",
            ["verification"]="Fresh unbounded entity-filtered payment queries, document and credit queries with IncludeLinkedTxns, and BillToPayQuery verify credit consumption, bidirectional links and remaining balances. Query any returned bill-payment stub IDs separately. An omitted transaction Amount is unknown, not numeric proof of zero cash impact.",
            ["liveScope"]="Test.QBW QBD2024R21 US protocol13: credit-only40 reduced one invoice and two bills100 to remaining60. ReceivePayment returned no payment ID and its fresh query was empty. Check and credit-card routes returned stubs with QuickBooks' zero-amount bill-payment-stub memo; TransactionQuery omitted numeric Amount. All three credit links, consumed credits, all/open normalized fields and mutation journals passed independent retained evidence verification. See creditOnlyApplicationChecks. No universal no-stub guarantee, numeric cash-impact, full-credit settlement, modify/unapply, multicurrency or other release proof.",
            ["documentationUrl"]="https://static.developer.intuit.com/resources/QBSDK_ProGuide.pdf"
        };
        result["paymentDistributionModifierContract"]=new JsonObject
        {
            ["memoOnly"]="ReceivePaymentMod and BillPaymentCheckMod can edit Memo/RefNumber without AppliedToTxnMod. Fresh EditSequence is required. Omitting the application aggregate preserved cash, discounts, classes, accounts and existing credit links on the tested host.",
            ["redistribution"]="Supplying AppliedToTxnMod replaces the payment/discount distribution; provide the complete intended set of applications, cash amounts and discount details. Existing credits are retained. SetCredit adds credits; it is not a replacement instruction for existing credits. Never resend existing credits to preserve them or assume this modifier can reduce/remove their applied amounts.",
            ["verification"]="Query the full payment applications immediately before planning and after apply. Compare all applications, linked credits, discount accounts/classes, totals and fresh document/credit balances. Payment/card/deposit/reconciliation context can affect acceptance; unknown outcomes are not automatically retried. An explicitly zero discount was omitted along with its account/class in the tested return; missing numeric fields remain unknown in the general response DTO.",
            ["liveScope"]="Test.QBW QBD2024R21 US protocol13: two memo edits preserved existing paid invoice/two bills and consumed credit links. Receive cash25/discount5 changed to20/10 with TotalAmount20. One bill check redistributed cash25/25 and discount5/5 to cash20/30 and discount10/0, retaining Amount50. Three application credit links and settled balances stayed unchanged; complete queried payment/document/credit fields and both mutation journals independently verified. See paymentDistributionModifierChecks. No existing-credit modification/unapply, partial redistribution, different-document reallocation, card modifier, multicurrency or other releases proof.",
            ["exampleWarning"]="Structural example only. Replace every placeholder with fresh company-scoped IDs/edit sequences and retain every intended application. This example does not authorize a payment change.",
            ["exampleFields"]=JsonNode.Parse("""
                {"ReceivePaymentMod":{"TxnID":"payment-id","EditSequence":"fresh-edit","TotalAmount":"20.00",
                "AppliedToTxnMod":{"TxnID":"invoice-id","PaymentAmount":"20.00","DiscountAmount":"10.00",
                "DiscountAccountRef":{"ListID":"discount-account-id"},"DiscountClassRef":{"ListID":"discount-class-id"}}}}
                """),
            ["documentationUrls"]=new JsonArray("https://developer.intuit.com/app/developer/qbdesktop/docs/api-reference/qbdesktop/receivepaymentmod","https://developer.intuit.com/app/developer/qbdesktop/docs/api-reference/qbdesktop/billpaymentcheckmod")
        };
        result["paymentReallocationContract"]=new JsonObject
        {
            ["request"]="ReceivePaymentMod/BillPaymentCheckMod with fresh payment EditSequence and AppliedToTxnMod listing the complete intended replacement cash/discount distribution. Select existing same-party documents by exact transaction ID. Existing credits stay on the original document when its cash/discount application is omitted; do not resend SetCredit to preserve those credits.",
            ["readback"]="The tested payment query returned only the replacement cash application; its absence of the original application/credit does not prove that credit was released. Query original and replacement documents plus their credit records with IncludeLinkedTxns, and BillToPayQuery for exact bill balances. Verify both directions of retained credit links and consumed credits. Never use BillQuery/VendorCreditQuery.OpenAmount as an individual document or available-credit balance.",
            ["liveScope"]="Test.QBW QBD2024R21 US protocol13: fresh same-party invoice pair100 and bill pair100. Original payment cash30/discount5/credit20 left45. Modifiers without SetCredit moved cash30/discount10 to the replacement, leaving original80 and replacement60. Original cash/discount links vanished while original bidirectional credit20 links stayed. Both payment queries returned only replacement applications. Eight normalized all/open document states, queried lines/unrelated headers, invoice AppliedAmount, exact BillToPay balances and journals independently verified. Bill and vendor-credit OpenAmount equalled the combined vendor balance145 then140. See paymentReallocationChecks. No cross-party/account, credit unapply, extra credits, deposited/reconciled payments, multicurrency, card modifier or other releases proof.",
            ["exampleWarning"]="Structural example only. Replace every placeholder with fresh confirmed IDs/edit sequence, inspect all existing applications and retain the complete intended replacement distribution. This does not authorize payment changes.",
            ["exampleFields"]=JsonNode.Parse("""
                {"BillPaymentCheckMod":{"TxnID":"existing-payment-id","EditSequence":"fresh-edit",
                "AppliedToTxnMod":{"TxnID":"replacement-bill-id","PaymentAmount":"30.00","DiscountAmount":"10.00",
                "DiscountAccountRef":{"ListID":"discount-account-id"},"DiscountClassRef":{"ListID":"discount-class-id"}}}}
                """),
            ["documentationUrl"]="https://developer.intuit.com/app/developer/qbdesktop/docs/api-reference/qbdesktop/billpaymentcheckmod"
        };
        result["additionalCreditModifierContract"]=new JsonObject
        {
            ["request"]="ReceivePaymentMod/BillPaymentCheckMod with a fresh EditSequence and complete intended cash/discount applications, plus a SetCredit-only AppliedToTxnMod for the target document and a newly available credit. SetCredit is additive: never resend existing applied credits merely to retain them. A standalone credit-only application list that omits existing cash/discount applications can replace those distributions and is outside this verified recipe.",
            ["verification"]="Query all original/new credit and target document IDs with IncludeLinkedTxns, then compare bidirectional links and exact remaining amounts. BillToPayQuery.CreditToApply.CreditRemaining establishes available vendor credits and BillToPay.AmountDue establishes bill balances. Fresh payment returns omitted the additional credit-only application on the tested host; a successful result must be verified from document/credit queries rather than requiring that application in the payment return.",
            ["balanceObservations"]="In this control ReceivePaymentQuery.UnusedCredits changed10->0 as the new customer credit was consumed. BillQuery and both VendorCreditQuery.OpenAmount remained130: unpaid bills140 minus available credit10 before application, and unpaid bills130 with no available credit after. This is scoped host evidence, not a universal balance rule; do not infer individual bill balances or credit availability from OpenAmount.",
            ["liveScope"]="Test.QBW QBD2024R21 US protocol13: fresh same-party invoice/bill pairs following reallocation. Complete modifier distributions retained replacement cash30/discount10 and added credit10 to original documents. Original credit20 remained unchanged; original remaining80->70 and replacement60 stayed unchanged. Both new credits fully consumed; eight normalized before/after states, full queried unrelated fields, both directions of links and mutation journals independently verified. See additionalCreditModifierChecks. No original-credit amount modification/unapply, standalone credit-only modifier omitting cash distributions, cross-party/account, card modifier, deposited/reconciled payments, multicurrency or other releases proof.",
            ["exampleWarning"]="Structural example only. Replace placeholders with fresh verified company IDs/EditSequence, confirm available credit, and retain every intended cash/discount application. This does not authorize payment changes.",
            ["exampleFields"]=JsonNode.Parse("""
                {"ReceivePaymentMod":{"TxnID":"existing-payment-id","EditSequence":"fresh-edit","AppliedToTxnMod":[
                {"TxnID":"existing-cash-target-invoice-id","PaymentAmount":"30.00","DiscountAmount":"10.00",
                "DiscountAccountRef":{"ListID":"discount-account-id"},"DiscountClassRef":{"ListID":"discount-class-id"}},
                {"TxnID":"additional-credit-target-invoice-id","SetCredit":{"CreditTxnID":"new-available-credit-id","AppliedAmount":"10.00"}}]}}
                """),
            ["documentationUrl"]="https://developer.intuit.com/app/developer/qbdesktop/docs/api-reference/qbdesktop/receivepaymentmod"
        };
        result["appliedPaymentCreditDeletionContract"]=new JsonObject
        {
            ["selection"]="TxnDel uses explicit TxnDelType and an exact confirmed transaction ID. Deleting a payment or a credit removes that transaction; it is a distinct lifecycle operation from payment redistribution, credit addition, credit unapply or voiding. Mutation company/apply/new-journal requirements remain mandatory.",
            ["acknowledgement"]="TxnDel responses expose scalar TxnDelType, TxnID and TimeDeleted with optional RefNumber directly in data; there is no invented TxnDelRet container. Verify the SDK acknowledgement and fresh surviving document/credit balances and links, not only the deletion status.",
            ["deletedIdQueries"]="The tested exact deleted-ID ReceivePayment/BillPaymentCheck/CreditMemo/VendorCredit queries returned warning500 with no data and a message naming the missing requested ID. The CLI retains outcome=warning and overall success=false/exit4. Empty data or warning500 alone is not universal deletion proof; correlate a successful durable delete acknowledgement, the exact requested ID and fresh surviving documents. Never replay deletion because a post-delete query reports a warning.",
            ["liveScope"]="Test.QBW QBD2024R21 US protocol13: two synthetic payments carrying cash30/discount10 deleted, restoring replacement invoice/bill60->100 while original invoice/bill70 retained independently applied credits20+10. Four credit transactions then deleted, restoring originals70->100 and removing all credit links. Twelve normalized all/open document states, complete queried unrelated fields, credit reverse links, exact deletion IDs/types and journals independently verified. See appliedPaymentCreditDeletionChecks. No voiding, original invoice/bill deletion, deposited/reconciled payments, multicurrency, general credit-unapply recipe or other releases proof.",
            ["documentationUrl"]="https://developer.intuit.com/app/developer/qbdesktop/docs/api-reference/qbdesktop/txndel"
        };
        result["appliedPaymentCreditVoidContract"]=new JsonObject
        {
            ["request"]="TxnVoid requires an explicit documented TxnVoidType and exact existing transaction ID, plus mutation company/apply/new-journal intent. ReceivePayment is not a documented TxnVoidType and is rejected by schema validation; do not invent receive-payment void support from deletion support.",
            ["acknowledgement"]="TxnVoid returns scalar TxnVoidType/TxnID/TimeCreated/TimeModified and optional RefNumber. On the tested host, voided check/card payments and vendor credits retained the same transaction IDs and remained directly queryable. Verify fresh record amounts, affected documents and links rather than inferring all effects from void status or memo text.",
            ["liveScope"]="Test.QBW QBD2024R21 US protocol13: two fresh bills100 with check/card cash30/discount5/credit20 applications initially45 remaining. Voiding payments retained records with Amount0 and VOID memo, removed cash/discount applications and left independent credits20, restoring bills80. Voiding vendor credits retained their records with CreditAmount0 and expense-line Amount0 plus VOID memo; credit links vanished and bills restored100. Six normalized all/open states, exact IDs/types/timestamps, links/reverse links, complete unrelated queried fields and journals independently verified. See appliedPaymentCreditVoidChecks. No other void types, deposited/reconciled payments, multicurrency or other releases proof.",
            ["documentationUrl"]="https://developer.intuit.com/app/developer/qbdesktop/docs/api-reference/qbdesktop/txnvoid"
        };
        result["documentBalanceContract"]=new JsonObject
        {
            ["invoices"]="InvoiceQuery.BalanceRemaining, unknown when absent.",
            ["bills"]="BillToPayQuery.AmountDue joined by exact transaction ID. One unbounded request per distinct unpaid vendor is submitted in bounded batches; never one query per bill. IsPaid=true confirms zero. Missing payable matches stay null. BillQuery.AmountDue is the original total and BillQuery.OpenAmount is not used as a per-bill balance.",
            ["evidence"]="Two distinct bills sharing one fresh vendor returned the combined vendor balance in BillQuery.OpenAmount at protocols13/17 with all IncludeLineItems/IncludeLinkedTxns combinations. BillToPayQuery returned distinct unpaid and partial amounts. One invoice and two bills passed unpaid/partial/paid normalized all/open lists; six explicit receive/check/card payment applications and links/journals passed independent saved-evidence verification. See partialDocumentPaymentChecks. Combined credit/discount applications have separate creditDiscountPaymentChecks; multicurrency and other releases remain unverified. Earlier documentSummaryChecks comparing OpenAmount establish historical source matching only, not correct per-bill balances."
        };
        result["salesLineClassContract"]=new JsonObject
        {
            ["status"]="bulk reclassify supports explicit Invoice, SalesReceipt and CreditMemo selection for confirmed service, non-inventory, other-charge, discount, payment items and inventory revenue-account lines, retaining nonposting subtotal items. Default types remain the original eight expense/journal/deposit types. Inventory requires exactly three actual postings with confirmed Income, OtherCurrentAsset and CostOfGoodsSold account types. COGS/asset account class selection, assemblies and other nonposting item kinds remain outside this planner.",
            ["descriptionOnlyLines"]="Retain a line with no ItemRef or OverrideItemAccountRef, nonblank Desc, absent/zero Amount, Quantity, Rate and RatePercent, and no joined account posting. Emit its existing TxnLineID and existing ClassRef only; never select a new class or infer an account for a nonposting description. Nonzero/malformed/itemless material lines and unidentified blank lines remain outside this recipe. Fresh explicit and public flat fixtures across Invoice/SalesReceipt/CreditMemo each changed six service classes and preserved six descriptions including existing classes, every other returned field and all postings. Public batches two/one and empty repeat preview passed independent verification. See salesDescriptionClassChecks and salesDescriptionBulkChecks. Group descriptions and other releases remain unverified.",
            ["singlePostingItems"]="Complete ItemServiceQuery, ItemNonInventoryQuery, ItemOtherChargeQuery, ItemDiscountQuery and ItemPaymentQuery identify single-posting item kinds, including inactive items and both shared/separate sales-purchase definitions. Every retained single-posting leaf requires exactly one actual posting whose amount magnitude matches its form amount. Preserve actual account overrides and existing classes; do not infer historical accounts from current item settings. Overlapping item-kind identities, incomplete queries, missing/ambiguous postings and unsupported retained leaves fail before writes. See additionalSalesItemBulkChecks for twelve forms/forty-eight classes across three submitted message sets of five/five/two requests.",
            ["selection"]="Read complete transaction-specific queries with IncludeLineItems=true and fresh EditSequence. IncludeLinkedTxns only where that query schema supports it; SalesReceiptQuery rejects that field. Select blank-class leaf lines by exact account ListID joined from complete TransactionQuery AllExceptSummary posting records using TxnType+TxnID+TxnLineID. Do not infer historical accounts from current item definitions or missing form OverrideItemAccountRef.",
            ["planning"]="For each transaction, retain all flat lines and original ordered group containers/children. Emit ordered $choices for TYPELineMod/TYPELineGroupMod. Each retained posting leaf carries its existing TxnLineID and OverrideItemAccountRef ListID from actual posting data. Confirmed ItemSubtotalQuery identities are retained without an account override or new class, and must have no account postings. Carry existing classes, and set the target ClassRef only on selected blank-class leaves. Keep unrelated amounts/items/rates/header/link fields omitted. One fresh EditSequence and one Mod per transaction permit bounded multi-request batches.",
            ["verification"]="After apply, query every changed form and complete posting set again. Verify selected ClassRef ListIDs through form queries: TransactionQuery posting rows do not return ClassRef. Compare all remaining form fields except TimeModified/EditSequence and all posting fields except TimeModified. Complete ItemServiceQuery, ItemInventoryQuery and all-active/inactive AccountQuery establish item kinds and account types, not historical account defaults. Inventory revenue must uniquely resolve to Income; asset/COGS amounts must balance. Missing/extra/unknown/ambiguous postings, amount mismatches or unsupported retained leaves abort before writes.",
            ["liveScope"]="Service fixtures with flat/group children, existing classes, overrides and a changed default; separate fresh inventory quantity/value fixtures with overrides and mixed service/inventory groups. Public bulk tests changed six classes per fixture across three forms, preserving returned fields and postings, with zero residual work. Inventory quantities/costs/definitions stayed unchanged. See salesBulkReclassificationChecks and inventoryBulkReclassificationChecks. Separate flat service/subtotal/fixed-discount explicit and public fixtures each changed nine classes in three forms; subtotal fields and all other returned form/posting fields were preserved. Public batch sizes two/one and empty repeat preview passed independent retained-evidence verification. See salesSpecialItemClassChecks and salesSpecialItemBulkChecks. Payment-item explicit proof changed nine classes in three forms; public bank-account selection changed three payment classes while preserving six service leaves, returned balances/links/fields and postings. Two/one mutation batches and empty repeat preview passed independent checks. See salesPaymentItemClassChecks and salesPaymentItemBulkChecks. Separate public five-decimal percentage/subtotal fixtures changed nine shared-account classes and three discount-only account classes, retaining six service leaves. Signed RatePercent, rounded subtotal discount calculation, full returned fields/postings, two/one batches and zero repeat changes passed independent evidence verification. Subtotal/discount/payment group contexts, sales-tax items, COGS/asset selection and other SDK contexts remain unverified.",
            ["exampleWarning"]="Structural example only. Replace every placeholder with fresh confirmed IDs/edit sequence, and retain every actual line and group child before applying. This shortened example is not a complete plan for any existing company transaction.",
            ["exampleFields"]=JsonNode.Parse("""
                {"InvoiceMod":{"TxnID":"existing-transaction-id","EditSequence":"fresh-edit","$choices":[
                  {"InvoiceLineMod":{"TxnLineID":"existing-flat-id","ClassRef":{"ListID":"target-class-id"},"OverrideItemAccountRef":{"ListID":"actual-flat-account-id"}}},
                  {"InvoiceLineGroupMod":{"TxnLineID":"existing-group-id","InvoiceLineMod":[
                    {"TxnLineID":"existing-selected-child-id","ClassRef":{"ListID":"target-class-id"},"OverrideItemAccountRef":{"ListID":"actual-child-account-id"}},
                    {"TxnLineID":"existing-other-child-id","ClassRef":{"ListID":"existing-class-id"},"OverrideItemAccountRef":{"ListID":"actual-other-account-id"}}
                  ]}}
                ]}}
                """)
        };
        result["purchaseLineClassContract"]=new JsonObject
        {
            ["status"]="bulk reclassify now supports confirmed service/non-inventory/other-charge and inventory purchase item leaves in Check, Bill, CreditCardCharge, CreditCardCredit, VendorCredit and ItemReceipt. Item and expense classes merge into one modifier per transaction. Default transaction types remain the original eight.",
            ["selection"]="Fresh type-specific forms with IncludeLineItems=true supply ItemLineRet/ItemGroupLineRet identities, classes and EditSequence. Complete TransactionQuery AllExceptSummary supplies actual accounts/amounts joined by TxnType+TxnID+TxnLineID. Service/non-inventory/other-charge leaves require one actual posting matching form amount magnitude. Inventory requires one amount-matched OtherCurrentAsset posting; credit forms may also have a balancing adjustment on the same asset account and one CostOfGoodsSold account. Select only the primary asset posting, never the COGS adjustment. Reject other, ambiguous or unbalanced shapes. Current item definitions or missing form overrides cannot establish the historical account.",
            ["planning"]="Retain every flat ItemLineRet and ordered ItemGroupLineRet container/child when modifying item classes. Emit ordered $choices ItemLineMod/ItemGroupLineMod with existing TxnLineID, actual OverrideItemAccountRef and existing/selected ClassRef. Do not send omitted item fields or clear lists. Merge selected expense classes into the same modifier, retaining every original ExpenseLineRet when that branch changes. One Mod per transaction uses fresh EditSequence. Item-only modifiers omit ExpenseLineMod; expense-only modifiers omit item branches. Complete known item identities and actual posting snapshots precede planning; unsupported retained items and ambiguous/missing joins abort item changes before writes.",
            ["verification"]="Read every changed form and complete posting set again. Verify selected classes through ItemLineRet, retain unselected classes and compare every other returned form field except TimeModified/EditSequence. Compare all posting fields apart from TimeModified. Missing/ambiguous joins, unknown item recipes and other posting shapes require separate evidence; no silent default-account inference.",
            ["liveScope"]="Test.QBW QuickBooks Accountant Desktop Plus2024R21 US protocol13.0: explicit item-only proof changed twenty-four classes; public combined proof changed thirty classes across six forms, retaining thirty-six item leaves/twelve expense lines. A separate thirty-class fixture preserved CustomerRef/Billable status on thirty-six allocated flat item/expense lines. Fresh form/posting reads, independent retained-evidence checks, two batches of three modifiers and empty repeat previews passed. See purchaseItemClassPreservationChecks, purchaseItemBulkReclassificationChecks and purchaseBillableClassChecks. Inventory purchase bulk additionally changed twelve classes across six forms in two batches of three; all returned postings/forms and inventory quantities/costs/definitions were preserved, with empty repeat preview. See purchaseInventoryClassChecks and purchaseInventoryBulkChecks. PO links, assemblies, changed inventory defaults, already-billed allocations, billable group children and other SDK contexts remain unverified.",
            ["linkedQueryFields"]="CheckQuery, BillQuery, VendorCreditQuery and ItemReceiptQuery include IncludeLinkedTxns=true for source and verification reads. CreditCardChargeQuery and CreditCardCreditQuery reject that field, so it is omitted. A linked non-inventory PO line plus billable allocations in BillAdd returned SDK3250 on Test.QBW; purchaseLinkProbeChecks preserves the exact combination without attributing the failing field or claiming all PO links unavailable. No successful linked-form preservation proof exists yet.",
            ["poLinkControls"]="Eight additional small controls omitted billable fields: BillAdd/ItemReceiptAdd, non-inventory/inventory items, LinkToTxnID whole-order and LinkToTxn individual-line branches. All returned SDK3250. The original first batch failed; process-aware journal resume selected only the four proven-unsubmitted receipt requests and excluded failed bills. Independent reads showed all eight POs still unreceived/unlinked and PreferencesQuery IsUsingInventory=false. That preference is an observation, not proven cause or universal availability gate. See purchaseOrderLinkControlChecks; linked-form class preservation remains unverified.",
            ["exampleWarning"]="Structural body only: wrap as TYPEMod for one of the supported types. Replace placeholders with fresh identities and retain every real flat/group child before applying; this shortened example is not a complete transaction plan.",
            ["exampleBody"]=JsonNode.Parse("""
                {"TxnID":"existing-transaction-id","EditSequence":"fresh-edit","$choices":[
                  {"ItemLineMod":{"TxnLineID":"existing-flat-id","OverrideItemAccountRef":{"ListID":"actual-flat-account-id"},"ClassRef":{"ListID":"target-class-id"}}},
                  {"ItemGroupLineMod":{"TxnLineID":"existing-group-id","ItemLineMod":[
                    {"TxnLineID":"existing-child-id","OverrideItemAccountRef":{"ListID":"actual-child-account-id"},"ClassRef":{"ListID":"existing-class-id"}}
                  ]}}
                ]}
                """)
        };
        result["callbackEventContract"]=new JsonObject
        {
            ["interface"]="IQBEventCallback IID A3A4E33E-E747-48CF-947B-93FDE67FF72F; IUnknown-based inform([in] BSTR) returns HRESULT. ABI verified from installed official sdkevent.dll type library.",
            ["listener"]="events listen requires explicit company path, new journal, and settings {callbackClsid:'{fresh GUID}',durationSeconds:1..43200,registerCallback:false|true}. registerCallback:true previews unless --apply; apply creates a fresh current-user CLSID registration owned by the process, removed on normal exit. No subscription is implicitly added. Existing registrations are never overwritten.",
            ["delivery"]="Use DeliverOnlyIfRunning with a manually started listener. events register supplies experimental persistent current-user configuration for packaged qbcli-callback.exe; it installs no SDK subscriptions. Directly launched helper processes passed synthetic callback checks, but native COM auto-launch currently fails with 0x80040154 on the tested PC. DeliverAlways support and actual QuickBooks delivery remain unverified. SDK subscriptions are installation-scoped and take effect when a company is opened; a successful add in an already-open company does not prove delivery. Menus may require QuickBooks restart. Keep the listener alive while the subscribed company is open.",
            ["input"]="Callback-only XML envelope QBXML/QBXMLEvents contains DataEvent, UIEvent or UIExtensionEvent. Each must carry one absolute CompanyFilePath. DTD/external entities, namespaces, documents >4 MiB and nested fields deeper than64 are rejected. Unknown field values are retained as data, never executed. Other companies are acknowledged but omitted from the journal.",
            ["journal"]="JSONL listener_starting, listener_ready, event:{receivedAt,notification:{eventType,companyFile,data}}, listener_finished. SDK element data retains name, attributes, value and children. Notifications are flushed to disk before callback acknowledgement. Stdout is one summary after listener termination. A failed callback returns E_FAIL and increments failedCallbacks; consult SDK event recovery rules. Abnormal process termination may leave its owned registration; events cleanup previews/removes only a stopped, owned current-user key with matching company scope. SDK subscriptions remain separate.",
            ["callbackRestrictions"]="Callback only persists notifications. It does not invoke SDK requests or modify company data; defer reads/writes to a separate SDK invocation after callback completion.",
            ["sources"]=new JsonArray("https://static.developer.intuit.com/resources/QBSDK_ProGuide.pdf","https://github.com/IntuitDeveloper/QBXML_SDK_Samples/tree/master/qbdt/c-sharp/qbXML/SubscribeAndHandleQBEvent_vs15")
        };
        result["sdkMessageSetContract"] = new JsonObject
        {
            ["input"]="Batch plans may include messageSetAttributes:{responseData:'includeAll|includeNone',newMessageSetID:'ID',oldMessageSetID:'ID'}. Strict JSON strings; company transport only.",
            ["ids"]="IDs are nonempty, <=23 characters, with no colon, slash or backslash. Recovery IDs require one message set, explicit --apply, company path and journal. Changing payload under a stored ID is rejected by SDK checksum validation.",
            ["clear"]="requests:[] plus oldMessageSetID (without newMessageSetID) emits a clearing-only message. ClearAllMessageSets affects all recovery records for this application/company; use individual IDs when possible.",
            ["outcomes"]="messageSetResponses preserves SDK envelope attributes and messageSetStatusCode. Nonzero message status fails the run. Missing per-request responses remain unknown even if the overall recovery status is zero. An empty clear requires an explicit zero SDK acknowledgement.",
            ["journals"]="Before submission the journal stores the exact internally generated XML plus IDs, typed plan and digest. XML is journal data only; the CLI never accepts arbitrary XML. sdk resume selects proven unexecuted work only; recovery-ID journals and unknown submissions require deliberate reconciliation.",
            ["responseData"]="includeNone suppresses response objects, not statuses. It cannot establish complete query records and is not compatible with --all.",
            ["source"]="https://static.developer.intuit.com/resources/QBSDK_ProGuide.pdf"
        };
        result["sdkInputContract"] = new JsonObject
        {
            ["fields"] = "Objects use actual XML tag names. Aggregates are nested objects; repeating fields use arrays. Invisible skip/OR schema groups are flattened. A single OR permits one branch; repeating OR permits multiple alternatives. Field order is constructed from the schema; grouped repeating alternatives follow schema order. For ordered interleaving supply $choices:[{Alternative:{fields}},{OtherAlternative:{fields}}] at the containing aggregate; each entry selects exactly one alternative. Do not mix $choices with grouped fields from the same choice.",
            ["attributes"] = "Use $attributes inside the corresponding object. Scalar attributes use {$value:VALUE,$attributes:{...}}. requestID is managed by the engine.",
            ["decimalValues"] = "Use strings for exact monetary/rate/quantity decimals. Response leaf values remain strings and retain original SDK precision.",
            ["dates"] = "yyyy-MM-dd; date-times use ISO yyyy-MM-ddTHH:mm:ss with optional offset. Null is rejected; omit optional fields. Empty string only where the SDK permits it.",
            ["batch"] = "{protocolVersion:'13.0',country:'US',onError:'stopOnError',requests:[{requestId:'1',operation:'ClassQuery',fields:{}}]}; shown in descriptive notation, actual input must be strict JSON.",
            ["limits"] = "Typed JSON input <=64 MiB, <=100000 requests, batches default 50 requests and <=1 MiB UTF-8, --batch-size 1..1000; batch JSON maximumBatchBytes integer 1024..16777216. Resume streams JSONL execution history without a whole-journal64 MiB cap; each record is limited to256 MiB UTF-8. These are application limits, not documented SDK maxima. JSON output buffers all results.",
            ["completeness"] = "sdk run/batch normally returns individual message results. sdk run OPERATION --all --page-size 500 completes supported iterator queries in the same session. Remaining count, iterator stability, duplicate identities and progress are checked. TransactionQuery posting identity is TxnType+TxnID+TxnLineID+optional AccountRef ListID+optional numeric Amount; inventory income/asset/COGS rows can share a line ID, and purchase credit rows can share even the account with distinct amounts. Summaries retain transaction-only identity; absent account/amount discriminators stay null. Amount identity uses invariant decimal equality, so equivalent text does not hide duplicates. Malformed amounts and repeated extended keys fail visibly; this is not a universal SDK posting identity guarantee. IncludeRetElement projections fetch missing identity fields including AccountRef/Amount internally and remove only those added fields. --all is rejected for non-iterator operations/direct lookup branches. Iterator IDs expire when the session ends; do not attempt continuation in a later CLI invocation.",
            ["journals"] = "Writes persist plan, submission intent, each outcome and completion before advancing. Default artifacts/runs/UUID.jsonl; --journal must be a new file in an existing directory. Journals contain company data and must be protected like exports. sdk resume previews/resubmits only never-submitted or SDK-acknowledged 3231 requests; unknown/warning submissions block apply. No automatic uncertain-write reconciliation or replay."
        };
        result["bulkChanges"]!["implemented"] = true;
        result["bulkChanges"]!["coverage"] = "SDK batches, durable journals, selective residual-plan resume and posting-line reclassification planning/apply/read-back are implemented. bulk reclassify supports exact-account blank-class expense lines in six transaction types plus journal debit/credit and deposit source-account lines; coverageCompleteForAllAccountPostings is false. Automatic uncertain-write reconciliation/replay is not implemented.";
        result["bulkChanges"]!["supportedTransactionTypes"] = new JsonArray(ReclassificationPlanner.SupportedTypes.Select(t=>(JsonNode?)JsonValue.Create(t)).ToArray());
        return result;
    }
    private static JsonNode JsonSerializerNode(ConnectionInfo value)
    {
        var node = JsonNode.Parse(Output.Json(value))!.AsObject();
        node["state"] = "connected";
        node["permissions"] = "HostQuery and CompanyQuery succeeded; consult companyFeatures for the optional PreferencesQuery probe. Other operations and write permissions have not been probed by this discovery command.";
        return node;
    }
}
