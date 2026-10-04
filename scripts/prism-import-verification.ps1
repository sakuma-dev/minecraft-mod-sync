#Requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Release', 'Debug')][string]$Configuration = 'Release')

# 実機担当が明示的に使う入口。画面を開くだけではPrismを操作しない。
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$sdk = Get-Content (Join-Path $repositoryRoot 'eng/dotnet-sdk.json') -Raw | ConvertFrom-Json
$sdkPath = Join-Path $env:LOCALAPPDATA ('ModSync/dotnet/' + $sdk.version + '-' + $sdk.rid)
$dotnet = Join-Path $sdkPath 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $installed = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $installed) { throw '固定SDKがありません。scripts/setup.ps1で準備してください。' }
    $dotnet = $installed.Source
    $sdkPath = Split-Path $dotnet -Parent
}
Push-Location $repositoryRoot
try {
    if ((& $dotnet --version) -ne $sdk.version) { throw '固定SDKのバージョンが一致しません。' }
    $env:DOTNET_ROOT = $sdkPath
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:MODSYNC_VERIFICATION_SDK = $sdk.version
    $env:MODSYNC_VERIFICATION_COMMIT = (& git rev-parse HEAD)
    if ($LASTEXITCODE -ne 0) { throw '対象コミットSHAを確認できません。' }
    $assembly = Join-Path $repositoryRoot "src/ModSync.Desktop/bin/$Configuration/net10.0-windows/ModSync.Desktop.dll"
    if (-not (Test-Path -LiteralPath $assembly)) { throw '先にscripts/dev.ps1 -Task Checkを実行してください。' }
    & $dotnet $assembly --prism-import-verification
    if ($LASTEXITCODE -ne 0) { throw "検証画面が終了コード$LASTEXITCODEで終了しました。" }
} finally { Pop-Location }
