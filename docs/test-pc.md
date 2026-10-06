# Test PC setup and live acceptance

## Install and configure

1. Use Windows and an activated, licensed QuickBooks Desktop installation. The first target is the installed U.S. Premier Accountant 2024 R21, with x64 request processor registration. CLI/QuickBooks should run under the same ordinary Windows user and elevation level. QuickBooks company administrator means the company login, not Windows elevation.
2. Publish the CLI with `./scripts/Publish.ps1`. The complete `artifacts/win-x64` directory includes .NET 10; no separate .NET runtime is needed for this distribution. Building from source requires the .NET 10 SDK and NuGet access for the test dependencies.
3. Run `./artifacts/win-x64/qbcli.exe diagnostics --json`. Check Windows=true, X64, STA, requestProcessorRegistered=true, comActivationTested=true and fullAccessPreferencesConfigured=true. This probe neither opens a connection nor accesses a company.
4. For SDK comparison tools/documentation, optionally install Intuit Desktop SDK 17.0 x64 from the [official download page](https://developer.intuit.com/app/developer/qbdesktop/docs/get-started/download-and-install-the-sdk). An Intuit Developer login and license acceptance are required for that download. The CLI does not need QBFC, Web Connector, an ODBC driver or a full SDK runtime installation.
5. If COM activation fails, verify process architecture and repair the licensed QuickBooks installation using Intuit's supported procedures. Do not copy/register random DLLs from another installation.

## Dedicated test company

The user created and opened:

```text
C:\QbCliTest\Test.QBW
```

For recreating it on another test PC, use QuickBooks' **Create a new company** workflow (Express Start/Start Setup or Detailed Start), choose a unique file name and finish setup. Use fictional company information. [Intuit company creation instructions](https://quickbooks.intuit.com/learn-support/en-us/help-article/set-files/create-quickbooks-desktop-company-file/L4oG2sQZh_US_en_US).

For account verification, add fictional accounts through QuickBooks: a bank account named `CLI Test Checking`, an expense account named `CLI Test Expense`, a subaccount, and an inactive account. Record the expected names/types/active flags from the chart of accounts. No balances or transactions are needed for this milestone. Use sdk run AccountAdd for schema-validated account creation with explicit apply intent.

Company files and their sidecars are excluded from Git. Store no production company files, exported real accounting data or credentials in the repository.

## Authorize and read

Open the test company in QuickBooks as its company administrator. Keep QuickBooks visible while running the first command:

```powershell
$testCompany = Join-Path $PWD 'QBData\Test.QBW'
./artifacts/win-x64/qbcli.exe company info --company-file $testCompany --json --verbose
./artifacts/win-x64/qbcli.exe accounts list --company-file $testCompany --json --verbose
./artifacts/win-x64/qbcli.exe company info --company-file $testCompany
./artifacts/win-x64/qbcli.exe accounts list --company-file $testCompany
```

When QuickBooks prompts for “McMurry Software QuickBooks CLI,” approve the requested read/write, personal-data and unattended profile; choose always allow access, including when QuickBooks is not running. This consistent profile was explicitly requested by the user. The CLI may block during BeginSession while the prompt is waiting. Authorization is an administrator action in QuickBooks; the CLI cannot grant itself access. [Intuit permissions guidance](https://quickbooks.intuit.com/learn-support/en-us/help-article/security-access/block-applications-accessing-quickbooks-company/L7JM4Ms8o_US_en_US).

**Observed on this PC, QuickBooks 2024 R21:** the user could not type `yes` until first opening the dialog's “Learn more about permissions of applications that integrate with QuickBooks” link. Open that link, return to the permission dialog, type `yes`, then Continue and complete any follow-up prompt. The initial screenshot showed the earlier read-only profile; the current profile requests full access. This extra link step is a local observation, not a verified requirement for every QuickBooks release.

To use an already-open company without the file option:

```powershell
./artifacts/win-x64/qbcli.exe company info --json
```

Verify the company before relying on the default-company behavior. With an explicit path, the SDK determines whether it can open that company; no file bytes are read by the adapter itself.

## Acceptance checklist

- [x] JSON company name matches the test file (`Test`), and JSON parses with ConvertFrom-Json.
- [ ] JSON accounts match the QuickBooks chart, including subaccounts and inactive accounts.
- [x] Human company/table output is readable.
- [x] Run both commands repeatedly; three additional pairs passed. Verbose initial reads confirmed EndSession, CloseConnection and COM release.
- [x] Subsequent runs succeeded without another authorization prompt while the test company remained open.
- [ ] Permission refusal/revocation yields structured failure and nonzero exit code; restore approval manually afterward.
- [ ] Validate attended multi-user mode with an appropriate licensed user configuration.
- [ ] If unattended operation is needed, explicitly grant “even when QuickBooks is not running,” then test with the exact scheduler identity/session configuration and explicit file path.

Only mark the relevant checks complete after observing them. Offline fake responses do not satisfy these checks. Company-mode changes remain a separate acceptance case; full authorization does not establish successful unattended execution.

## Logging and automation

`--verbose` writes operation names, timings, cleanup status, exception type and HRESULT to stderr. It does not log raw XML, accounting values, ticket strings or file paths. Capture privately:

```powershell
./artifacts/win-x64/qbcli.exe accounts list --company-file $testCompany --json --verbose 2> qb-diagnostics.log
$qbExitCode = $LASTEXITCODE
```

For SDK failures, consult Intuit's QBSDKLog.txt on the test PC; its location depends on installation. The SDK's own log may contain more sensitive details than CLI logs. SDKTestPlus3 is useful to distinguish CLI problems from QuickBooks authorization/installation failures. Do not commit logs or captured company data.

There is no automatic COM timeout or unattended retry loop in this milestone. A modal authorization dialog can hold a process open. Avoid parallel CLI invocations against one company while evaluating connection reliability.


