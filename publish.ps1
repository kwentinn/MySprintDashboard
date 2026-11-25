# Publishes the dashboard into the publish\ folder (see "As an executable" in the README): dashboard.exe with a copy
# of the page (wwwroot) and of dashboard.settings.json next to it, so the folder runs on its own and opens the browser.
# A running dashboard.exe locks its file: it is stopped first, and the new one is started. -NoStart leaves it stopped.
#
#   .\publish.ps1
param([switch]$NoStart)

$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot 'publish'
$exe = Join-Path $target 'dashboard.exe'

# The dashboard of this repository, wherever it runs from (publish\, or this folder for an older publish): its
# file cannot be replaced while it runs, and two cannot listen on the same port.
$running = @(Get-Process dashboard -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $PSScriptRoot '*') })
$wasRunning = $running | Select-Object -First 1 -ExpandProperty Path
if ($running) {
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

dotnet publish (Join-Path $PSScriptRoot 'dashboard.csproj') -o $target -r win-x64 -p:PublishAot=false -p:PublishSingleFile=true --self-contained false
if ($LASTEXITCODE -ne 0) {
    # A failed build leaves the executable as it was: the dashboard that was stopped is started again.
    if ($wasRunning -and -not $NoStart) { Start-Process $wasRunning -WorkingDirectory (Split-Path $wasRunning) }
    throw "dotnet publish failed ($LASTEXITCODE): publish\ is unchanged."
}

# The page (wwwroot) and the settings of this folder go with it: the executable reads them next to itself. The page
# is replaced whole, so a file removed here does not stay there.
$page = Join-Path $target 'wwwroot'
if (Test-Path $page) { Remove-Item $page -Recurse -Force }
# An earlier layout had index.html next to the executable: it is no longer read.
Remove-Item (Join-Path $target 'index.html') -ErrorAction SilentlyContinue
Copy-Item (Join-Path $PSScriptRoot 'wwwroot') $target -Recurse
Copy-Item (Join-Path $PSScriptRoot 'dashboard.settings.json') $target -Force
Write-Host "Published in $target ($((Get-Item $exe).LastWriteTime))."

# It opens the browser itself.
if (-not $NoStart) { Start-Process $exe -WorkingDirectory $target }
