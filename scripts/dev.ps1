#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Build', 'Check', 'Smoke', 'Run')]
    [string]$Task = 'Check',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$sdk = Get-Content (Join-Path $repositoryRoot 'eng/dotnet-sdk.json') -Raw | ConvertFrom-Json
$sdkPath = Join-Path $env:LOCALAPPDATA ('ModSync/dotnet/' + $sdk.version + '-' + $sdk.rid)
$dotnet = Join-Path $sdkPath 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $installed = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $installed) { throw 'SDK not found. Run .\scripts\setup.ps1 first.' }
    $dotnet = $installed.Source
    $sdkPath = Split-Path $dotnet -Parent
}

function Invoke-DotNet {
    & $dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}

Push-Location $repositoryRoot
try {
    $version = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $version -ne $sdk.version) { throw 'Required SDK not found. Run .\scripts\setup.ps1 first.' }
    $env:DOTNET_ROOT = $sdkPath
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    Invoke-DotNet restore ModSync.slnx --locked-mode
    if ($Task -eq 'Run') {
        Invoke-DotNet run --project src/ModSync.Desktop --configuration $Configuration --no-restore
    } else {
        Invoke-DotNet build ModSync.slnx --configuration $Configuration --no-restore
        if ($Task -eq 'Check') {
            Invoke-DotNet test ModSync.slnx --configuration $Configuration --no-build --no-restore --logger trx --results-directory artifacts/TestResults
        } elseif ($Task -eq 'Smoke') {
            $output = Join-Path $repositoryRoot ('artifacts/startup/' + [Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $output | Out-Null
            $assembly = Join-Path $repositoryRoot "src/ModSync.Desktop/bin/$Configuration/net10.0-windows/ModSync.Desktop.dll"
            $arguments = @(('"' + $assembly + '"'), '--smoke-test', ('"' + $output + '"'))
            $startInfo = New-Object System.Diagnostics.ProcessStartInfo
            $startInfo.FileName = $dotnet
            $startInfo.Arguments = $arguments -join ' '
            $startInfo.UseShellExecute = $false
            $startInfo.CreateNoWindow = $true
            $startInfo.WindowStyle = 'Hidden'
            $startInfo.RedirectStandardError = $true
            $process = New-Object System.Diagnostics.Process
            $process.StartInfo = $startInfo
            try {
                $process.Start() | Out-Null
                $errorRead = $process.StandardError.ReadToEndAsync()
                if (-not $process.WaitForExit(30000)) {
                    $process.Kill()
                    throw 'WPF startup did not finish within 30 seconds.'
                }
                $stderr = $errorRead.GetAwaiter().GetResult()
                [IO.File]::WriteAllText((Join-Path $output 'stderr.log'), $stderr)
                if ($process.ExitCode -ne 0) { throw "WPF startup failed with exit code $($process.ExitCode). See $output" }
                $result = Get-Content (Join-Path $output 'startup.json') -Raw | ConvertFrom-Json
                if (-not $result.isLoaded -or $result.title -ne 'Minecraft MOD Sync' -or $result.width -le 0 -or $result.height -le 0) {
                    throw 'WPF window did not load correctly.'
                }
                if ($result.assemblySha256 -ine (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash) {
                    throw 'Core/Platform file hash did not match the built application.'
                }
                if ((Get-Item (Join-Path $output 'startup.png')).Length -eq 0) { throw 'WPF did not render an image.' }
                Write-Host "WPF startup passed. Evidence: $output"
            } finally { $process.Dispose() }
        }
    }
} finally { Pop-Location }
