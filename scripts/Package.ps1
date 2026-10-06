param(
    [ValidateSet('win-x64','win-x86')][string]$Runtime = 'win-x64',
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipPublish) { & (Join-Path $PSScriptRoot 'Publish.ps1') -Runtime $Runtime }
$buildPath = Join-Path $projectRoot "artifacts\$Runtime"
foreach ($binaryName in @('qbcli.exe', 'qbcli-callback.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $buildPath $binaryName))) { throw "Missing $binaryName; publish first." }
}
if (Get-ChildItem -LiteralPath $buildPath -Recurse -File | Where-Object { $_.Name -match '(?i)QBXMLRP|QBFC|\.QBW$|\.QBB$|\.QBM$|\.TLG$|\.ND$|\.csv$|\.jsonl$' }) {
    throw 'The distribution contains an SDK DLL or company/import data that must not be packaged.'
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $buildPath -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $buildPath -Recurse -Force
$projectXml = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\QbCli\QbCli.csproj') -Raw)
$version = $projectXml.Project.PropertyGroup.Version
$zipPath = Join-Path $projectRoot "artifacts\qbcli-$version-$Runtime.zip"
Compress-Archive -Path (Join-Path $buildPath '*') -DestinationPath $zipPath -Force
$digest = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$digestPath = "$zipPath.sha256"
[IO.File]::WriteAllText($digestPath, "$digest  $([IO.Path]::GetFileName($zipPath))`n", [Text.UTF8Encoding]::new($false))
Write-Output $zipPath
Write-Output $digestPath
