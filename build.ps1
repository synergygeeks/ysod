# build.ps1
# Compiles YsodGateModule.cs into a DLL using csc.exe from .NET Framework.
# No Visual Studio or SDK required for bin\ builds.
# GAC builds (-ForGac) require sn.exe from the Windows SDK.
#
# Usage:
#   .\build.ps1              - builds for bin\ deployment (PoC / dev testing)
#   .\build.ps1 -ForGac      - builds with strong name (production GAC deployment)

param(
    [switch]$ForGac
)

$ErrorActionPreference = 'Stop'

# --- PATHS ---

$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceFile = Join-Path $scriptDir 'src\YsodGateModule.cs'
$outputDll  = Join-Path $scriptDir 'YsodGateModule.dll'
$snkFile    = Join-Path $scriptDir 'YsodGateModule.snk'

$fxDir  = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319'
$cscExe = Join-Path $fxDir 'csc.exe'

# --- VALIDATE ---

if (-not (Test-Path $sourceFile)) {
    Write-Error "Source file not found: $sourceFile"
}

if (-not (Test-Path $cscExe)) {
    Write-Error "csc.exe not found at $cscExe. Is .NET Framework 4.x installed?"
}

# --- BUILD ---

$cscArgs = @(
    '/target:library'
    '/optimize+'
    "/out:$outputDll"
    '/reference:System.dll'
    '/reference:System.Web.dll'
    '/reference:System.Configuration.dll'
    $sourceFile
)

if ($ForGac) {
    # Strong naming is required for GAC installation.
    # Locate sn.exe from the Windows SDK.
    $snExe = Resolve-Path "${env:ProgramFiles(x86)}\Microsoft SDKs\Windows\*\bin\NETFX*\sn.exe" `
             -ErrorAction SilentlyContinue | Select-Object -First 1

    if (-not $snExe) {
        Write-Error @"
sn.exe not found. Strong naming requires the Windows SDK or Visual Studio.
Install the .NET Framework SDK tools, or build on a machine that has them.
Alternatively, omit -ForGac for a bin\ deployment build (no strong name needed).
"@
    }

    if (-not (Test-Path $snkFile)) {
        Write-Host "Generating strong-name key: $snkFile"
        & $snExe.Path -k $snkFile
        if ($LASTEXITCODE -ne 0) { Write-Error 'sn.exe key generation failed.' }
    }

    $cscArgs += "/keyfile:$snkFile"
    Write-Host 'Building with strong name (GAC deployment)...'
}
else {
    Write-Host 'Building without strong name (bin\ deployment)...'
}

& $cscExe @cscArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error 'Compilation failed. See csc.exe output above.'
}

Write-Host ''
Write-Host "Build succeeded: $outputDll" -ForegroundColor Green

if ($ForGac) {
    $asm      = [System.Reflection.Assembly]::LoadFrom($outputDll)
    $fullName = $asm.FullName
    Write-Host ''
    Write-Host 'Full assembly name (copy this into web.config):' -ForegroundColor Cyan
    Write-Host "  $fullName"
    Write-Host ''
    Write-Host 'Next steps:'
    Write-Host "  1. gacutil.exe /i `"$outputDll`""
    Write-Host '  2. Add the module registration to root web.config (see README.md)'
}
else {
    Write-Host ''
    Write-Host 'Next steps:'
    Write-Host '  1. Copy DLL to the site bin\ folder:'
    Write-Host "     Copy-Item `"$outputDll`" `"C:\inetpub\wwwroot\<site>\bin\`""
    Write-Host '  2. Add the module registration to the site web.config (see README.md)'
}
