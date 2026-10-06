# CSV import preparation

qbcli currently accepts schema-validated SDK JSON; it has no built-in `import csv` command. CSV imports can use one JSON batch containing many requests, submitted in bounded message sets through one QuickBooks session. Determine the CSV's record type and field mapping before generating that plan.

## On the second machine

Use a licensed Windows QuickBooks Desktop installation and its registered request processor. Follow [test PC setup](test-pc.md), including first-time company-administrator authorization. Download and extract the entire Windows x64 release ZIP, keeping all its files together. The release includes .NET; building from source instead requires the SDK version in `global.json`.

```powershell
$qbcli = 'C:\Tools\qbcli\qbcli.exe'
$testCompany = 'C:\QbCliTest\Test.QBW' # Set the actual test-company path.
& $qbcli diagnostics --json
& $qbcli company info --company-file $testCompany --json
& $qbcli capabilities --json > capabilities.json
```

The automated live fixture scripts in this repository are guarded to the original developer's disposable Test.QBW path. They are not a portable import runner. Offline `dotnet test McMurry.QbCli.sln` requires no QuickBooks connection.

## Prepare the import

1. Identify what each CSV row represents: a list record, a complete transaction, or one line of a transaction. Capture encoding, column names, date/decimal formats and how rows are grouped. Keep real CSV/company data in `local-data/` or another private location outside tracked source.
2. Choose the documented SDK operation and inspect `sdk schema OPERATION`. Resolve referenced customers, vendors, accounts, items, classes and existing transactions from the target company. IDs are company-specific. Preserve external source IDs separately so a later reconciliation can distinguish imported records from duplicates.
3. Generate JSON with a unique `requestId` per intended SDK request. Group all lines of one transaction into its single add request. Explicit SDK macros can connect newly created records only where the schema documents macro attributes; otherwise create/query dependencies first and use confirmed IDs.
4. Validate the whole plan and review an offline mutation preview. A typical plan has `protocolVersion`, `country` and `requests`, where each request has `requestId`, `operation` and `fields`. Set request-count bounds with the CLI `--batch-size` option; `batchSize` is not an accepted batch JSON field. For example, a customer row can map to `CustomerAdd` with `fields: {"CustomerAdd":{"Name":"Synthetic CSV Customer"}}`. This is a structural example, not the coworker's data mapping.
5. Run a small representative import against a disposable test company. Verify fresh SDK records, references, amounts, dates, line grouping and document balances before applying the complete plan.

```powershell
# Mutation-only plans preview without --apply. Mixed plans containing reads can
# connect, so use sdk validate per operation for strictly offline validation.
& $qbcli sdk batch --input 'local-data\import-plan.json' --json

# Apply only after mapping and preview have been reviewed for this company.
& $qbcli sdk batch --input 'local-data\import-plan.json' --apply `
  --company-file $testCompany --journal 'local-data\import-run.jsonl' --json
```

The journal path must be new. Preserve it and the exact plan/results privately for reconciliation. Batches are not atomic: earlier requests may have succeeded when a later request fails. Do not rerun the entire import after an error or uncertain outcome. Inspect correlated results and fresh company records; `sdk resume` permits only a proven-unexecuted remainder and does not resolve an unknown write automatically.

For tomorrow, bring the CSV header, representative anonymized rows, the intended QuickBooks record type, reference mapping rules and expected totals. Those details determine the converter and its verification rather than assuming every CSV is a customer or transaction import.
