# Retakes the README screenshots (docs\*.png) from fictional data: a copy of wwwroot with demo-data.js loaded first,
# served on localhost, and Chrome headless. Nothing is read from Azure DevOps or from the vault.
#
#   .\docs\demo\make-screenshots.ps1            all of them
#   .\docs\demo\make-screenshots.ps1 -Only today,pbi
param([string[]]$Only)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$docs = Join-Path $repo 'docs'
$site = Join-Path $env:TEMP 'dashboard-demo-site'
$port = 5199
$chrome = @('C:\Program Files\Google\Chrome\Application\chrome.exe', 'C:\Program Files (x86)\Google\Chrome\Application\chrome.exe') | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $chrome) { throw 'Chrome is not installed in its usual folders.' }

# The page as it is, with the demo script before its own.
if (Test-Path $site) { Remove-Item $site -Recurse -Force }
Copy-Item (Join-Path $repo 'wwwroot') $site -Recurse
Copy-Item (Join-Path $PSScriptRoot 'demo-data.js') $site
# The light theme, whatever the machine uses: the dark rules (a media block, one level of braces inside) are removed.
$css = Join-Path $site 'css\app.css'
[regex]::Replace((Get-Content $css -Raw), '@media \(prefers-color-scheme: dark\) \{(?:[^{}]|\{[^{}]*\})*\}', '') | Set-Content $css -NoNewline
$index = Join-Path $site 'index.html'
(Get-Content $index -Raw).Replace('<script type="module"', '<script src="demo-data.js"></script>' + "`n" + '<script type="module"') | Set-Content $index -NoNewline

# name, height, address (language, what to open, the page)
$shots = @(
    @{ Name = 'today';         Height = 1340; Address = '?lang=en' },
    @{ Name = 'notifications'; Height = 1000; Address = '?lang=en&demo=notifications' },
    @{ Name = 'pbi';           Height = 1120; Address = '?lang=en#pbi/1201/03-job-done' },
    @{ Name = 'pr';            Height = 1000; Address = '?lang=en#pr/4204/threads' },
    @{ Name = 'questions';     Height = 900; Address = '?lang=en#questions' },
    @{ Name = 'search';        Height = 1750; Address = '?lang=en#search/invoice' },
    @{ Name = 'daily';         Height = 1120; Address = '?lang=en&demo=summary#daily' }
)
if ($Only) { $shots = $shots | Where-Object { $Only -contains $_.Name } }

$server = Start-Process node -ArgumentList "`"$(Join-Path $PSScriptRoot 'serve.mjs')`"", "`"$site`"", $port -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Seconds 1
    foreach ($shot in $shots) {
        $out = Join-Path $docs "$($shot.Name).png"
        # The window size is quoted: a comma outside quotes makes PowerShell pass two arguments.
        & $chrome --headless=new --disable-gpu --hide-scrollbars "--window-size=1400,$($shot.Height)" --force-device-scale-factor=1 `
            "--user-data-dir=$env:TEMP\dashboard-demo-chrome" --virtual-time-budget=9000 `
            "--screenshot=$out" "http://localhost:$port/$($shot.Address)" 2>$null | Out-Null
        Write-Host "$($shot.Name).png"
    }
}
finally {
    Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
}
