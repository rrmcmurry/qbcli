> Historical milestones below. Current 0.3 SDK commands and permission profile are documented in [sdk-commands.md](sdk-commands.md); earlier read-only restrictions have been superseded by the explicitly authorized SDK phase.

# CLI contract, schema version 1

Transaction history commands (0.8.8): `transactions list [--since YYYY-MM-DD] [--until YYYY-MM-DD]` and `transactions search --memo TEXT` with the same optional cutoffs. Both accept --page-size/company-file/file-mode/json/verbose. Dates select inclusive transaction dates, not modification timestamps. Search retrieves every selected page before applying ordinal case-insensitive substring matching to summary memos; scannedRecords counts the complete selected query before filtering. No line memo or item/expense/group description search is implied.

The adapter sends TransactionQuery SummaryOnly through the existing checked SDK iterator and normalizes id/type/date, optional number/signed amount/memo and optional entity/account/currency references. Identity is (type,id); rows sort ordinal date/type/id. Amount is the SDK summary value, not a calculated balance or posting total. Missing amounts/currency stay unknown. Unexpected posting-line IDs, malformed/duplicate fields, missing identities, invalid dates/amounts and a returned date outside the requested range fail explicitly. Later-page errors cannot produce partial-success search output.

complete means this permitted SDK query finished. TransactionQuery respects the current user's permissions, sensitive-data level and feature/subscription restrictions; TimeTracking is excluded by the SDK. The result includes coverageScope and does not claim every transaction in the company. Other SDK operations remain available for type-specific/line/time data. Automated handler/iterator tests and retained-summary normalization checks exist; fresh live execution of the new list/search paths remains pending. See [Intuit's generic transaction query guide](https://static.developer.intuit.com/qbSDK-current/doc/pdf/QBSDK_ProGuide.pdf).

Financial report commands (0.8.7): `reports trial-balance --as-of YYYY-MM-DD`, `reports balance-sheet --as-of YYYY-MM-DD`, and `reports profit-loss --from YYYY-MM-DD --to YYYY-MM-DD`. Dates are required, validated before connection, and use the SDK's inclusive report period. Optional `--basis accrual|cash` defaults accrual. Company/file-mode/JSON/verbose options remain available. Commands send GeneralSummaryReportQuery with DisplayReport=false and ReturnRows/ReturnColumns=All. Unsupported host country/protocol or SDK failures remain errors. No GUI report is opened.

Normalized reports retain title, subtitle, actual basis, explicit selection, multi-row column titles, column IDs/types/meanings, ordered text/data/subtotal/total rows, row numbers and optional row entity labels. Cells keep their column ID, effective value type, exact text and an optional decimal amount for nonblank monetary values. A per-cell type overrides the column type. Absent cells, absent values, blank strings and explicit zero remain distinct. No currency, ListID or additional hierarchy is inferred. Do not add subtotal/total rows to compute another total. Human tables preserve headings and column alignment. Missing optional basis is unknown; selection.basis is only the requested basis.

Report completeness requires declared row/column counts to match, unique positive row/column IDs, valid cell references, no duplicate cell/title IDs and supported row/value types. Reports have no iterator in this interface; they are bounded by the shared32MiB character response limit and1M rows/1000 columns/title rows. Partial, malformed or unsupported responses fail without reporting complete success. Tests and retained-live-data normalization checks exist; fresh live execution through these new handlers remains pending. See [Intuit's report response guide](https://static.developer.intuit.com/resources/QBSDK_ProGuide.pdf).

Invoice and bill summary commands (0.8.6): `qbcli invoices list [--open]` and `qbcli bills list [--open]`. Both accept `--page-size`, `--company-file`, `--file-mode`, `--json` and `--verbose`. They complete the SDK iterator before emitting results, with the same one-million-record bound and fail-on-incomplete behavior as generic SDK pagination. The query uses qbXML13.0 and the advertised host country; unsupported imported iterator country/version requirements fail explicitly. Live verification of these new convenience handlers is pending.

Results include company/path, documentType, openOnly, complete, pages and normalized results. Each summary has id, nullable number, date, nullable dueDate, party `{id,fullName}`, nullable remainingAmount, nullable paid, nullable memo and nullable currency `{id,name}`. Invoice remainingAmount comes from BalanceRemaining; bill remainingAmount comes from BillToPayQuery.AmountDue joined by exact TxnID, with one unbounded query per distinct unpaid vendor submitted in bounded SDK batches. A confirmed paid bill reports zero; otherwise a missing payable match stays null. BillQuery.OpenAmount is not used because distinct bills returned the aggregate vendor balance on the test host. BillQuery.AmountDue is the original bill total and is never substituted. Currency omission stays unknown; no home-currency assumption or cross-currency total is made. Amounts use decimal JSON numbers. Records sort ordinal date, number, ID. Human output prints Unknown for absent amount/currency/paid status and neutralizes control characters.

`--open` uses the SDK's PaidStatus=NotPaidOnly, including partially paid documents. Without it, PaidStatus=All includes paid and unpaid documents. An unexpected paid or known zero-balance response in open mode fails instead of asserting a valid unpaid result. Unknown optional fields remain unknown. The commands retrieve summaries without lines/links. Unknown/malformed/duplicate identity, date, amount or boolean fields fail visibly. A later page failure emits only the error envelope. See [Intuit's query filter documentation](https://static.developer.intuit.com/resources/QBSDK_ProGuide.pdf).

Commands: `qbcli capabilities`, `qbcli company info`, `qbcli accounts list`, `qbcli diagnostics`, customer/vendor `list`, `find "TEXT"`, and `get --id ID` or `get --name "EXACT FULL NAME"`. Common read options: `--json`, `--verbose`, `--company-file <absolute .QBW path>`. `--help`/`-h` displays help. Unknown commands/options and incomplete paths fail before connection.

`capabilities` always emits JSON and works without QuickBooks. It includes all current command definitions, options, examples, descriptive output contracts, errors, limits, attribution, proposed bulk workflow and 239 documented SDK operation names/links. Its embedded command registry is also the parser's allowlist. The SDK catalog distinguishes implemented operations from documentation-only entries; it does not expose arbitrary XML execution. Result contracts use descriptive notation rather than formal JSON Schema.

`capabilities --live` additionally queries HostQuery and CompanyQuery in a single message set, validates response requestIDs and reports actual company path, host product/version, country, automatic login, file mode and supported qbXML versions. It then makes an optional read-only PreferencesQuery in the same session, reporting selected configuration/access flags under companyFeatures. Missing/malformed/duplicate flags stay unknown; the optional probe may fail with a sanitized error while host discovery remains useful. A company mismatch fails the command. Feature flags do not establish individual operation availability or write permission. A company path on discovery requires --live. Host versions are not a complete edition/authorization matrix. Offline connection is `{ "state": "not_connected" }` and company feature facts are not queried.

Customer/vendor list and find include inactive records, read all iterator pages, and return `{success:true,schemaVersion:1,company:"Test",entityType:"customer",complete:true,results:[]}`. Each record contains id, name, fullName, nullable active and nullable companyName. Results sort ordinal fullName then ID. Find matches case-insensitive substrings of full names, including customer jobs, after complete retrieval. Exact get accepts exactly one opaque ListID or case-insensitive exact full name; successful get also returns a one-element results array. Missing entities return not_found, exit 4; ambiguous exact results return ambiguous_match. Empty list/find is a successful complete empty array.

--page-size is available only for customer/vendor list/find, default 500, range 1..1000. It bounds each request, not final output size. Commands buffer up to one million records; each XML response is limited to 32 MiB of characters. Iterator failures, duplicate IDs, non-progress and warnings fail without reporting partial data as success. SDK 500/Warn with no records is normalized to not_found only for explicit single-object lookup, retaining diagnosticCode 500.

`--json` writes exactly one document to stdout for success or failure; diagnostics and capabilities always return JSON. Diagnostic logs are separate on stderr. Human command failures go to stderr. Help supports JSON. Nothing writes raw QBXML to the public streams.

Company result example (synthetic):

```json
{
  "success": true,
  "schemaVersion": 1,
  "company": "McMurry SDK Test",
  "info": { "name": "McMurry SDK Test", "legalName": null }
}
```

Account result example (synthetic):

```json
{
  "success": true,
  "schemaVersion": 1,
  "company": "McMurry SDK Test",
  "accounts": [
    { "id": "opaque-id", "name": "Checking", "fullName": "Assets:Checking", "type": "Bank", "active": true }
  ]
}
```

IDs are opaque, company-scoped strings. Account ordering is ordinal fullName then ID. Active and inactive accounts are included. `active` can be null if the SDK omits its optional flag; the table shows Unknown. Company and legal names can be null if absent. Missing required account identity/name/type values fail instead of being fabricated. Empty results are `accounts: []`. No account balances, tax IDs or bank numbers are exposed by these DTOs.

Failure example:

```json
{
  "success": false,
  "schemaVersion": 1,
  "error": {
    "code": "quickbooks_connection",
    "message": "QuickBooks communication failed. Open the test company as administrator, authorize McMurry Software QuickBooks CLI, and check the QuickBooks SDK log.",
    "diagnosticCode": "0x80040418"
  }
}
```

The HRESULT above illustrates format only; callers must not infer its meaning from this example. diagnosticCode contains an SDK status code or HRESULT when available, otherwise null. Connection/error envelopes omit raw SDK XML and stack traces. SDK per-request statusMessage is preserved in execution JSON and journals and can contain company values; it is omitted from diagnostic stderr.

| Exit code | Meaning |
| --- | --- |
| 0 | Successful result/help/diagnostic report |
| 2 | Invalid CLI arguments |
| 3 | Unsupported platform or COM registration failure |
| 4 | QuickBooks/file/session/query failure |
| 5 | Unexpected application error |

Diagnostic success means the environment report completed; examine its booleans. `liveConnectionTested` is always false for diagnostics. The current probe activates COM and configures the stable full-access process-local authorization profile without OpenConnection2/BeginSession. It reports requestedAccess, fullAccessPreferencesConfigured and the legacy readOnlyPreferencesConfigured=false field. It does not connect to or change a company.

The schema is provisional for this first milestone. Additive fields should remain compatible; breaking field/meaning changes require a new schemaVersion. Accounting types are currently strings. Future decimal amounts must use decimal domain values and explicitly documented accounting/currency semantics.

Current SDK execution/preview results distinguish planned accounting/list, recovery, subscription and UI effects with `mutatesCompany`, `mutatesRecoveryState`, `mutatesSubscriptions` and `affectsUserInterface`. These are possible effects of the submitted/prepared plan, not proof of success. Version0.8.3 corrects the earlier overbroad classification of recovery deletion and form-display requests as accounting mutations. All non-query apply/path/journal guards remain intact. Correlated outcomes and fresh reads establish actual changes. The SDK operation records and journal plan header carry the same categories; older journal headers may omit them.

