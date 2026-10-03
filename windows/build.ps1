<#
.SYNOPSIS
  Builds BigFiles.exe: one self-contained file for 64-bit Windows (no .NET install needed to run it).

.DESCRIPTION
  Needs the .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0) to build.
  Output: dist\BigFiles.exe and dist\BigFiles-win-x64.zip

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build.ps1
  powershell -ExecutionPolicy Bypass -File .\build.ps1 -Run
#>
param(
    [switch]$Run,        # start the app when the build is done
    [switch]$SkipTests   # don't run the logic tests first
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "The .NET 8 SDK is needed to build BigFiles: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
    exit 1
}

function Invoke-Step([string]$what, [scriptblock]$cmd) {
    Write-Host "==> $what" -ForegroundColor Cyan
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

# The icon is drawn in code (tools\IconGen); regenerate it so it always matches the generator.
Invoke-Step 'Drawing the icon' {
    dotnet run --project tools\IconGen -c Release -- src\BigFiles\Assets\BigFiles.ico icon.png
}

if (-not $SkipTests) {
    Invoke-Step 'Running the logic tests' { dotnet run --project tests\BigFiles.Tests -c Release }
}

$dist = Join-Path $PSScriptRoot 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

Invoke-Step 'Publishing BigFiles.exe (single file, self-contained, win-x64)' {
    dotnet publish src\BigFiles\BigFiles.csproj -c Release -o $dist
}

$exe = Join-Path $dist 'BigFiles.exe'
$zip = Join-Path $dist 'BigFiles-win-x64.zip'
Compress-Archive -Path $exe -DestinationPath $zip -Force

$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Done: $exe ($mb MB)" -ForegroundColor Green
Write-Host "      $zip"

if ($Run) { Start-Process $exe }
