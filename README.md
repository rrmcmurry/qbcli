# qbcli by McMurry Software

Keep QuickBooks. Make it scriptable.

**Developed by McMurry Software. Not developed, sponsored, or endorsed by Intuit. McMurry Software is not affiliated with Intuit.**

Independent command-line SDK tool for QuickBooks Desktop on Windows. The executable is `qbcli`. It is useful from PowerShell and automation without requiring AI.

Implemented: comprehensive offline capability discovery, typed SDK schema/validation/execution, bounded multi-request batches with durable journals and linked SDK macros, selective journal resume, iterator pagination, bulk expense/journal/deposit and opt-in service/non-inventory/other-charge/inventory revenue sales line reclassification, a COM event receiver, and company/account/customer/vendor reads, invoice/bill summaries, transaction history/memo search and financial reports. Service/non-inventory/other-charge leaves require one actual posting; inventory requires confirmed item/account types and exactly three income/asset/COGS postings. COGS/asset account class selection remains unsupported. Normalized summary/report handlers have automated coverage and fresh independent SDK comparisons on Test.QBW. Verification remains scoped to the tested fields, fixtures and release. No MCP server is implemented.

**Status:** 231 company-operation schemas and seven subscription schemas through qbXML 13.0, plus selected documented SDK14/16 fields; protocols up to17.0 are accepted for known fields. This is not complete SDK17 coverage. Live tests on QuickBooks Accountant Desktop Plus 2024 R21 accepted 204 distinct operations, including query smoke probes, synthetic add/modify/delete/void/merge requests and all seven subscription operations. Smoke acceptance does not verify every field or lifecycle. Capability output records exact test outcomes and limitations. Actual QuickBooks event delivery after a new company-open cycle, unattended operation with QuickBooks shut down, multi-user operation, other countries and other releases remain untested.

Bulk purchase selection now includes service/non-inventory/other-charge item lines in the six default purchase types. Item and expense classes share one modifier per transaction, with complete retained flat/group lists and fresh form/posting verification. Billable customer fields on flat purchase item/expense lines have scoped preservation evidence. Flat purchase inventory has scoped explicit/public preservation coverage. PO links, already-billed allocations and billable group children still need successful lifecycle coverage.

## Run

For another Windows machine, start with [test PC setup](docs/test-pc.md). Build from source with the pinned .NET SDK, or download the complete Windows x64 ZIP from this private repository's Releases. The ZIP includes the .NET runtime; QuickBooks Desktop and its registered request processor are still required locally. See [CSV import preparation](docs/csv-import.md) for tomorrow's import workflow.

From the repository root in PowerShell:

```powershell
./scripts/Publish.ps1
./artifacts/win-x64/qbcli.exe diagnostics --json
./artifacts/win-x64/qbcli.exe capabilities
./artifacts/win-x64/qbcli.exe capabilities --live
./artifacts/win-x64/qbcli.exe customers list --json
./artifacts/win-x64/qbcli.exe vendors find 'Rental' --json
./artifacts/win-x64/qbcli.exe company info --json --verbose
./artifacts/win-x64/qbcli.exe accounts list
./artifacts/win-x64/qbcli.exe invoices list --open --json
./artifacts/win-x64/qbcli.exe bills list --open --json
./artifacts/win-x64/qbcli.exe reports trial-balance --as-of 2026-10-05 --json
./artifacts/win-x64/qbcli.exe reports profit-loss --from 2026-01-01 --to 2026-12-31 --json
./artifacts/win-x64/qbcli.exe reports balance-sheet --as-of 2026-10-05 --json
./artifacts/win-x64/qbcli.exe transactions list --since 2026-01-01 --json
./artifacts/win-x64/qbcli.exe transactions search --memo insurance --json
```

Use the currently open company, or select a test file explicitly:

```powershell
./artifacts/win-x64/qbcli.exe accounts list --company-file 'C:\QbCliTest\McMurry SDK Test.QBW' --json
if ($LASTEXITCODE -ne 0) { throw 'QuickBooks query failed' }
```

The path above is an example, not an existing company. Read [test PC setup](docs/test-pc.md) before connecting. Published output includes .NET; keep the entire output folder together. Add that folder to your session's PATH if you want to type `qbcli` directly.

## Build and test

Requires .NET SDK 10.0.303 (or a newer 10.0.3xx patch accepted by `global.json`).

```powershell
dotnet build McMurry.QbCli.sln
dotnet test McMurry.QbCli.sln
```

Offline tests use synthetic responses and fake request processors; they never connect to QuickBooks. The CLI alone owns command parsing/output, Core owns DTOs and read services, and QuickBooks owns COM/session/QBXML handling.

See [technical design](docs/design.md), [SDK research](docs/sdk-research.md), [JSON contract](docs/cli-contract.md), and [milestone results](docs/milestone-status.md).

`capabilities` is the comprehensive discovery dump for an AI or script: commands, options, input/output contracts, errors, operation links, structural field schemas and sanitized live test coverage. Repeated schemas are interned in `sdkReference.schemaNodes`; follow ordered references or use `sdk schema OPERATION` for an expanded schema. `--live` adds actual host/company metadata and a read-only snapshot of selected SDK preference flags. Missing flags stay unknown; configuration observations do not establish individual SDK operation availability or write permission.

```powershell
./artifacts/win-x64/qbcli.exe sdk schema CheckMod
./artifacts/win-x64/qbcli.exe sdk validate CustomerAdd --input fields.json
./artifacts/win-x64/qbcli.exe sdk run CustomerQuery --all --page-size 500
./artifacts/win-x64/qbcli.exe sdk batch --input plan.json
./artifacts/win-x64/qbcli.exe sdk batch --input plan.json --apply --company-file 'C:\QbCliTest\Test.QBW'
./artifacts/win-x64/qbcli.exe bulk reclassify --input selection.json --company-file 'C:\QbCliTest\Test.QBW'
./artifacts/win-x64/qbcli.exe sdk resume --input prior-run.jsonl --company-file 'C:\QbCliTest\Test.QBW'
```

Mutations preview by default; applying requires `--apply` and an explicit company path. All sessions request one consistent read/write, personal-data and unattended authorization profile. An administrator must grant it in QuickBooks. Batch execution is non-atomic and never automatically retries unknown writes. See [SDK command contract](docs/sdk-commands.md) and [bulk change design](docs/bulk-changes.md).

Company sessions accept `--file-mode any|single-user|multi-user` (default `any`). This requests the SDK's BeginSession mode. It cannot switch a company already open in another mode. Discovery reports the intent as `connection.requestedFileMode` and the actual HostQuery observation as `connection.host.fileMode`. Some mutations, including changing an item's posting account, require single-user mode. The option keeps the same authorization identity and permissions. See the [Intuit SDK guide](https://static.developer.intuit.com/resources/QBSDK_ProGuide.pdf).

See [SDK event callbacks](docs/sdk-events.md) for `events listen`, experimental persistent `events register`, subscription lifecycle and owned-registration cleanup. The packaged helper passed synthetic callbacks when directly launched; COM auto-launch currently fails on the test PC, so DeliverAlways remains unverified. The receiver stores notifications only; it never performs accounting changes from a callback.


Version 0.8.22 extends purchase bulk reclassification to confirmed inventory leaves in Check, Bill, CreditCardCharge, CreditCardCredit, VendorCredit and ItemReceipt. Selection uses the historical amount-matched OtherCurrentAsset posting. Credit forms may include a balanced adjustment on the same asset account plus a CostOfGoodsSold posting; those postings are preserved and cannot select the item class. Ambiguous or other shapes abort before writes. Fresh Test.QBW proof changed twelve classes in six forms, retained thirty-six flat/group item leaves and all expense lines, preserved all returned postings/forms and inventory quantities/costs/definitions, and left zero repeat-preview changes. This does not prove assemblies, PO links, changed defaults or other releases.


Version 0.8.23 adds discount sales-line class selection and subtotal retention to explicit Invoice/SalesReceipt/CreditMemo bulk reclassification. Complete active/inactive ItemDiscountQuery and ItemSubtotalQuery establish identities. A discount requires one historical posting with amount magnitude matching its form line; a subtotal must have no account posting and is retained by identity without a new class or account override. Fresh explicit SDK and public bulk fixtures each changed nine classes across three forms while preserving subtotal fields and all other returned form/posting fields. Public mutations were batched two/one, repeat preview had zero changes, and independent retained evidence verification passed. Automated suite: 691 passing tests. Percentage discounts, subtotal/discount groups, payment/sales-tax items and other releases remain unverified. The full SDK goal remains active.


Version 0.8.24 adds confirmed payment-item sales-line class selection to explicit Invoice/SalesReceipt/CreditMemo bulk reclassification. Complete active/inactive ItemPaymentQuery establishes identities. Each retained payment leaf requires one historical account posting whose amount magnitude matches its form line; current item deposit-account defaults are not used as historical evidence. Fresh explicit SDK proof changed nine classes in three forms. Separate public bank-account selection changed three payment classes while retaining six unselected service leaves, all returned balances/links/form fields and all postings. Two/one mutation batches, zero repeat changes and independent retained evidence checks passed. Automated suite: 698 passing tests. Sales-tax items, percentage discounts, special-item groups, changed payment defaults and other releases remain unverified. The overall SDK goal remains active.


Version 0.8.25 adds a derived liveVerificationSummary to every SDK catalog entry and a liveCoverageSummary for the full dump. States distinguish recorded SDK success, accepted empty queries only, attempted without acceptance, and no retained attempt. Outcome counts preserve failures/warnings/unprocessed/unknown alongside successes; status codes and protocols are summarized without claiming current-company availability or exhaustive field/lifecycle verification. Counts describe retained fixture outcomes, not independent sessions.

Fresh private-extension deletion controls succeeded with single-user BeginSession requested for both an unused definition and one assigned to a customer with a value still present. Definition/customer readback and independent retained evidence verified absence after deletion. The first assigned control omitted AssignToObject:Customer and returned3180; a distinct fresh definition with that documented assignment field succeeded. Earlier3175 evidence remains visible; no causal claim about the mode or owner difference is made. No public custom-field deletion or deleted-name recreation is claimed. Automated suite: 703 passing tests; the overall SDK goal remains active.


Version 0.8.26 enforces Intuit OSR numeric formats for PRICETYPE, QUANTYPE, PERCENTTYPE and FLOATTYPE before SDK connection. Price/quantity/percent permit10 integer and5 fractional digits at protocol8+, with documented smaller integer formats below8; older PRICETYPE requires a decimal point. FLOATTYPE permits8 integer,6 fractional and9 total digits. Limits count transmitted digits excluding sign/point, preserve original text and never round/truncate. Percentages may be negative or exceed100 unless a specific field has another restriction. sdkNumericContract exposes exact rules and primary-source URLs. AMTTYPE keeps existing validation because OSR amount text and SDK13 release-note formats conflict; no universal precision rule is guessed.

Fresh Test.QBW service/discount/invoice writes and queries preserved five-decimal price, rate, quantity and discount percentage magnitude. Positive discount input/definition returned negative invoice RatePercent. The initial verifier expected the wrong sign after all writes/reads succeeded; independent retained evidence verified the observed sign and precision without replay. Numeric precision metadata includes hashes of the official cached type pages. No extreme integer-size, older host/protocol or live FLOATTYPE proof is claimed. All730 automated tests pass. The overall SDK goal remains active.


Version 0.8.27 retains confirmed description-only lines when sales posting lines are reclassified in Invoice, SalesReceipt and CreditMemo. Description lines require no ItemRef/account override, nonblank text, absent/zero Amount/Quantity/Rate/RatePercent and no joined account posting. They carry existing TxnLineID/ClassRef and receive no new class/account. Nonzero or malformed itemless lines and unidentified empty lines still reject changes. Separate fresh explicit SDK and public fixtures each changed six service classes and preserved six descriptions, including existing classes, all other returned form fields and every posting. Public batches two/one, empty repeat preview and independent retained evidence checks passed. Group descriptions and other releases remain unverified. All739 automated tests pass. The overall SDK goal remains active.


Version 0.8.28 records a fresh private-extension deletion/recreation lifecycle: same OwnerID/name re-add returned SDK3180 and remained absent; a different fresh private owner accepted the same name, with readback and deletion/absence verified. Independent saved plans and five mutation journals passed verification. Callback diagnostics now use the current distribution and record activation context, client bitness and helper hash. Native local, forced64-bit local and all-local COM controls still returned0x80040154 before a receiver journal appeared; two directly launched current-build helper processes passed synthetic callback filtering, durable acknowledgement and owned cleanup. Actual QuickBooks event delivery and automatic helper launch remain unverified. All739 automated tests pass; the overall SDK goal remains active.


Version 0.8.29 adds public bulk percentage-discount preservation evidence for Invoice, SalesReceipt and CreditMemo. Two fresh controls changed nine shared-account classes and three discount-account-only classes; the latter retained six service leaves. Both preserved the signed five-decimal RatePercent, subtotal21.00, rounded discount-2.55, every other returned form field and every posting. Each used two/one mutation batches and had an empty repeat preview. Independent saved plans, modifier bodies, fresh snapshots and journals passed verification without replay. Discovery now distinguishes this flat percentage-discount evidence from unverified group/sales-tax contexts. All739 automated tests pass; the overall SDK goal remains active.


Version 0.8.30 resolves the BOX16 schema conflict using the public Intuit SDK features page. US BOX16 now accepts protocol16+, and BOX14 uses documented disjoint intervals below14 and16..<17. Query/Mod country/version boundary tests pass. Live read-only controls accepted MISC BOX16 at16/17, rejected NEC BOX16 with1000 and rejected BOX14 at16 with3110 on the current host. Twenty saved request/results were independently verified and exposed through discovery, preserving schema/host distinctions. No mapping modifications or automatic migration were performed. All745 automated tests pass. SDK17 installer/release notes still require an unavailable Intuit sign-in; complete SDK17 coverage and the broader goal remain active.


Version 0.8.31 fixes normalized per-bill balances after a live lifecycle exposed misleading BillQuery.OpenAmount. Two different bills sharing one fresh vendor returned their combined balance in both records at protocols13/17 and every line/link projection combination. Normalized bills now join BillToPayQuery.AmountDue by exact TxnID, with one unbounded request per distinct unpaid vendor in bounded batches. Confirmed paid bills report zero; missing unpaid matches stay null. BillQuery.AmountDue remains original total. The existing fixture continued from successful saved seed without replay: invoice/check-paid bill/card-paid bill passed unpaid/partial/paid states, all/open membership and every normalized summary field. Six payments, links and journals passed independent saved-evidence verification. Earlier source comparisons to OpenAmount did not prove per-bill accounting correctness and are superseded. Credits/discounts/multicurrency/other releases remain unverified. All751 automated tests pass; the broader SDK goal remains active.


Version 0.8.32 adds a documented creditDiscountApplicationContract with typed example and scoped live application evidence. A fresh invoice100 received cash25/credit20/discount5, leaving50; bills100/80 shared a vendor credit20 split10/10 with cash25/discount5 each, leaving60/40. Fresh payment queries confirmed all three discount amounts/accounts/classes and credit links. Credits were consumed; subsequent cash completion yielded paid documents absent from open lists. Independent plans, four mutation batch journals, fresh SDK reads and every normalized summary field verified six partial/paid states. No credit-only lean return, overpayment, auto-allocation, applied-credit modification/unapply, multicurrency or other releases proof. All751 automated tests pass; the full SDK goal remains active.
