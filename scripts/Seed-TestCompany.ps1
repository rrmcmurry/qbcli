param([Parameter(Mandatory)][string]$CompanyFile,
      [string]$Executable = (Join-Path $PSScriptRoot '../artifacts/win-x64/qbcli.exe'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$designated = [IO.Path]::GetFullPath((Join-Path $root 'QBData/Test.QBW'))
if (![string]::Equals([IO.Path]::GetFullPath($CompanyFile), $designated, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Live mutation testing is restricted to the explicitly designated QBData/Test.QBW.'
}
$fixtureDirectory = Join-Path $root 'artifacts/fixtures'
New-Item -ItemType Directory -Path $fixtureDirectory -Force | Out-Null
$prefix = 'SDK' + (Get-Date -Format 'HHmmss')
$calls = [Collections.Generic.List[object]]::new()
function Add-Request([string]$operation,[hashtable]$body) {
    $fields = @{}; $fields[$operation] = $body
    $calls.Add(@{requestId=($calls.Count+1).ToString();operation=$operation;fields=$fields})
}
foreach ($entry in @(
    @('Bank','Bank'), @('Card','CreditCard'), @('Income','Income'), @('Expense','Expense'),
    @('AR','AccountsReceivable'), @('AP','AccountsPayable'), @('COGS','CostOfGoodsSold'),
    @('Inventory','OtherCurrentAsset'), @('Asset','OtherCurrentAsset'), @('Equity','Equity')
)) { Add-Request 'AccountAdd' @{Name="$prefix $($entry[0])";AccountType=$entry[1]} }
Add-Request 'CustomerAdd' @{Name="$prefix Customer"}
Add-Request 'VendorAdd' @{Name="$prefix Vendor"}
Add-Request 'EmployeeAdd' @{FirstName='SDK';LastName=$prefix}
Add-Request 'ClassAdd' @{Name="$prefix Class"}
Add-Request 'CustomerTypeAdd' @{Name="$prefix CustomerType"}
Add-Request 'VendorTypeAdd' @{Name="$prefix VendorType"}
Add-Request 'JobTypeAdd' @{Name="$prefix JobType"}
Add-Request 'PaymentMethodAdd' @{Name="$prefix Method"}
Add-Request 'ShipMethodAdd' @{Name="$prefix Ship"}
Add-Request 'StandardTermsAdd' @{Name="$prefix Net30";StdDueDays=30}
Add-Request 'CustomerMsgAdd' @{Name="$prefix Message"}
Add-Request 'ItemServiceAdd' @{Name="$prefix Service";SalesOrPurchase=@{Price='25.00';AccountRef=@{FullName="$prefix Income"}}}
Add-Request 'ItemNonInventoryAdd' @{Name="$prefix Supplies";SalesAndPurchase=@{SalesDesc='Test supplies';SalesPrice='25.00';IncomeAccountRef=@{FullName="$prefix Income"};PurchaseDesc='Test supplies';PurchaseCost='10.00';ExpenseAccountRef=@{FullName="$prefix Expense"}}}
Add-Request 'ItemOtherChargeAdd' @{Name="$prefix Charge";SalesOrPurchase=@{Price='5.00';AccountRef=@{FullName="$prefix Income"}}}
Add-Request 'ItemDiscountAdd' @{Name="$prefix Discount";DiscountRate='1.00';AccountRef=@{FullName="$prefix Income"}}
Add-Request 'ItemSubtotalAdd' @{Name="$prefix Subtotal"}
Add-Request 'ItemPaymentAdd' @{Name="$prefix Payment";DepositToAccountRef=@{FullName="$prefix Bank"}}
$plan = @{protocolVersion='13.0';country='US';onError='continueOnError';requests=$calls.ToArray()}
$planPath = Join-Path $fixtureDirectory "$prefix-base-plan.json"
$resultPath = Join-Path $fixtureDirectory "$prefix-base-result.json"
$plan | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $planPath -Encoding utf8
& $Executable sdk batch --input $planPath --company-file $CompanyFile --apply --batch-size 50 > $resultPath
$result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
@{prefix=$prefix;plan=$planPath;result=$resultPath;success=$result.success;outcomes=$result.results | Select-Object operation,outcome,statusCode} | ConvertTo-Json -Depth 8
if ($null -ne $result.error) { throw ($result.error | ConvertTo-Json -Compress) }
