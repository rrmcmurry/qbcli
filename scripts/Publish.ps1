param([ValidateSet('win-x64','win-x86')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $projectRoot "artifacts\$Runtime"
& dotnet publish (Join-Path $projectRoot 'src\QbCli\QbCli.csproj') -c Release -r $Runtime --self-contained true -o $outputPath -p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
& dotnet publish (Join-Path $projectRoot 'src\QbCli.CallbackServer\QbCli.CallbackServer.csproj') -c Release -r $Runtime --self-contained true -o $outputPath -p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) { throw 'Callback server publish failed.' }
# Remove only the obsolete product files from this generated distribution.
foreach ($legacyName in @('qb.exe', 'qb.dll', 'qb.deps.json', 'qb.runtimeconfig.json', 'qb.pdb')) {
    $legacyPath = Join-Path $outputPath $legacyName
    if (Test-Path -LiteralPath $legacyPath) { Remove-Item -LiteralPath $legacyPath }
}
Write-Host "Published to $outputPath. Run qbcli.exe diagnostics --json first."

