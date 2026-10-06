param([Parameter(Mandatory)][string]$CompanyFile, [Parameter(Mandatory)][string]$Prefix,
      [string]$Executable = (Join-Path $PSScriptRoot '../artifacts/win-x64/qbcli.exe'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$designated = [IO.Path]::GetFullPath((Join-Path $root 'QBData/Test.QBW'))
if (![string]::Equals([IO.Path]::GetFullPath($CompanyFile),$designated,[StringComparison]::OrdinalIgnoreCase)) { throw 'Only designated Test.QBW is authorized for mutation tests.' }
if ($Prefix -notmatch '^SDK[0-9]{6}$') { throw 'Use the prefix from Seed-TestCompany.ps1.' }
$calls = [Collections.Generic.List[object]]::new()
function Add-Request([string]$operation,[hashtable]$body) {
    $fields = @{}; $fields[$operation] = $body
    $calls.Add(@{requestId=($calls.Count+1).ToString();operation=$operation;fields=$fields})
}
function Ref([string]$suffix) { return @{FullName="$Prefix $suffix"} }
$date = '2026-10-05'
Add-Request 'InvoiceAdd' @{CustomerRef=(Ref 'Customer');ARAccountRef=(Ref 'AR');TxnDate=$date;Memo='SDK test invoice';InvoiceLineAdd=@{ItemRef=(Ref 'Service');Quantity='2';Rate='25.00'}}
Add-Request 'BillAdd' @{VendorRef=(Ref 'Vendor');APAccountRef=(Ref 'AP');TxnDate=$date;Memo='SDK test bill';ExpenseLineAdd=@{AccountRef=(Ref 'Expense');Amount='10.00'}}
Add-Request 'CheckAdd' @{AccountRef=(Ref 'Bank');PayeeEntityRef=(Ref 'Vendor');TxnDate=$date;Memo='SDK test check';ExpenseLineAdd=@{AccountRef=(Ref 'Expense');Amount='20.00'}}
Add-Request 'CreditCardChargeAdd' @{AccountRef=(Ref 'Card');PayeeEntityRef=(Ref 'Vendor');TxnDate=$date;ExpenseLineAdd=@{AccountRef=(Ref 'Expense');Amount='15.00'}}
Add-Request 'CreditCardCreditAdd' @{AccountRef=(Ref 'Card');PayeeEntityRef=(Ref 'Vendor');TxnDate=$date;ExpenseLineAdd=@{AccountRef=(Ref 'Expense');Amount='5.00'}}
Add-Request 'SalesReceiptAdd' @{CustomerRef=(Ref 'Customer');DepositToAccountRef=(Ref 'Bank');TxnDate=$date;SalesReceiptLineAdd=@{ItemRef=(Ref 'Service');Quantity='1';Rate='25.00'}}
Add-Request 'CreditMemoAdd' @{CustomerRef=(Ref 'Customer');ARAccountRef=(Ref 'AR');TxnDate=$date;CreditMemoLineAdd=@{ItemRef=(Ref 'Service');Quantity='1';Rate='5.00'}}
Add-Request 'EstimateAdd' @{CustomerRef=(Ref 'Customer');TxnDate=$date;EstimateLineAdd=@{ItemRef=(Ref 'Service');Quantity='1';Rate='25.00'}}
Add-Request 'PurchaseOrderAdd' @{VendorRef=(Ref 'Vendor');TxnDate=$date;PurchaseOrderLineAdd=@{ItemRef=(Ref 'Supplies');Quantity='2';Rate='10.00'}}
Add-Request 'JournalEntryAdd' @{TxnDate=$date;JournalDebitLine=@{AccountRef=(Ref 'Expense');Amount='100.00';Memo='SDK test journal'};JournalCreditLine=@{AccountRef=(Ref 'Income');Amount='100.00';Memo='SDK test journal'}}
Add-Request 'DepositAdd' @{TxnDate=$date;DepositToAccountRef=(Ref 'Bank');DepositLineAdd=@{AccountRef=(Ref 'Income');Amount='100.00'}}
Add-Request 'VendorCreditAdd' @{VendorRef=(Ref 'Vendor');APAccountRef=(Ref 'AP');TxnDate=$date;ExpenseLineAdd=@{AccountRef=(Ref 'Expense');Amount='2.00'}}
$directory = Join-Path $root 'artifacts/fixtures'
$planPath = Join-Path $directory "$Prefix-transactions-plan.json"
$resultPath = Join-Path $directory "$Prefix-transactions-result.json"
@{protocolVersion='13.0';country='US';onError='continueOnError';requests=$calls.ToArray()} | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $planPath -Encoding utf8
& $Executable sdk batch --input $planPath --company-file $CompanyFile --apply > $resultPath
$result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
@{prefix=$Prefix;result=$resultPath;success=$result.success;outcomes=$result.results | Select-Object operation,outcome,statusCode} | ConvertTo-Json -Depth 8
if ($null -ne $result.error) { throw ($result.error | ConvertTo-Json -Compress) }
