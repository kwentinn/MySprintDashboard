# Publishes the project again as dashboard.exe in this folder (see "As an executable" in the README).
# A running dashboard.exe of this folder locks the file: it is stopped first, and started again once the new one is
# in place. -NoStart leaves it stopped.
#
#   .\publish.ps1
param([switch]$NoStart)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'dashboard.exe'
$output = Join-Path $env:TEMP 'dashboard-publish'

# Publish first: if the build fails, the running dashboard is left alone.
dotnet publish (Join-Path $PSScriptRoot 'dashboard.csproj') -o $output -r win-x64 -p:PublishAot=false -p:PublishSingleFile=true --self-contained false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE): dashboard.exe is unchanged." }

$running = @(Get-Process dashboard -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
if ($running) {
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

Copy-Item (Join-Path $output 'dashboard.exe') $exe -Force
Write-Host "dashboard.exe published ($((Get-Item $exe).LastWriteTime))."

# Started again when it was running, and on a first publish; it opens the browser itself.
if (-not $NoStart) { Start-Process $exe -WorkingDirectory $PSScriptRoot }
