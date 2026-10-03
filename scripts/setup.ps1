#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$sdk = Get-Content (Join-Path $repositoryRoot 'eng/dotnet-sdk.json') -Raw | ConvertFrom-Json
$globalConfig = Get-Content (Join-Path $repositoryRoot 'global.json') -Raw | ConvertFrom-Json
if ($sdk.version -ne $globalConfig.sdk.version) { throw 'SDK versions in global.json and eng/dotnet-sdk.json differ.' }
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitProcess) {
    throw 'Run this setup from 64-bit PowerShell on Windows 11.'
}

$installParent = Join-Path $env:LOCALAPPDATA 'ModSync/dotnet'
$installPath = Join-Path $installParent ($sdk.version + '-' + $sdk.rid)
$dotnet = Join-Path $installPath 'dotnet.exe'
if (Test-Path -LiteralPath $dotnet) {
    Push-Location $repositoryRoot
    try {
        $version = & $dotnet --version
        if ($LASTEXITCODE -ne 0 -or $version -ne $sdk.version) { throw 'The existing SDK does not match. Inspect the installation before retrying.' }
        Write-Host "SDK $version is already installed: $installPath"
        return
    } finally { Pop-Location }
}
if (Test-Path -LiteralPath $installPath) { throw "An incomplete installation exists: $installPath. Inspect it before retrying." }

$archive = [IO.Path]::GetTempFileName()
$staging = Join-Path $installParent ($sdk.version + '-install-' + [Guid]::NewGuid().ToString('N'))
try {
    Write-Host "Downloading .NET SDK $($sdk.version)..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $sdk.url -OutFile $archive -UseBasicParsing
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ine $sdk.sha512) {
        throw 'SDK archive SHA-512 does not match Microsoft release metadata. Installation stopped.'
    }
    New-Item -ItemType Directory -Path $installParent -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $staging)
    # Both paths are within the dedicated SDK directory; never replace another installation.
    $allowedPrefix = [IO.Path]::GetFullPath($installParent).TrimEnd('\') + '\'
    foreach ($target in @($staging, $installPath)) {
        if (-not [IO.Path]::GetFullPath($target).StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'SDK path is outside the installation directory.'
        }
    }
    Move-Item -LiteralPath $staging -Destination $installPath
    Push-Location $repositoryRoot
    try {
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        $version = & $dotnet --version
        if ($LASTEXITCODE -ne 0 -or $version -ne $sdk.version) { throw 'Installed SDK version verification failed.' }
        Write-Host "Verified SDK ${version}: $installPath"
        Write-Host 'Next: .\scripts\dev.ps1 -Task Check'
    } finally { Pop-Location }
} finally {
    Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
}
