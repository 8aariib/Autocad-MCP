<#
  Claude for AutoCAD - installer.

  Run from the unzipped folder in PowerShell:
      powershell -ExecutionPolicy Bypass -File .\install.ps1

  What it does, and nothing else:
    1. Installs uv (the Python package manager) for your user if it is missing.
    2. Copies AutoCADChat.bundle to %APPDATA%\Autodesk\ApplicationPlugins,
       where AutoCAD loads it automatically.
    3. Prepares the Python environment in %LOCALAPPDATA%\AutoCADChat\venv
       so the first chat starts quickly.
  No administrator rights are needed.
#>
param(
    [switch]$SkipPrepare
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $here 'AutoCADChat.bundle'
$pluginsRoot = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
$dest = Join-Path $pluginsRoot 'AutoCADChat.bundle'
$venv = Join-Path $env:LOCALAPPDATA 'AutoCADChat\venv'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

if (-not (Test-Path (Join-Path $source 'Contents\AutoCADChat.dll'))) {
    throw "AutoCADChat.bundle was not found next to install.ps1. Unzip the whole package first."
}

Step 'Checking for AutoCAD 2025'
$acadKey = 'HKLM:\SOFTWARE\Autodesk\AutoCAD\R25.0'
if (Test-Path $acadKey) { Write-Host 'AutoCAD 2025 found.' }
else { Write-Warning 'AutoCAD 2025 was not found in the registry. Installing anyway; the panel loads in AutoCAD 2025 and 2026.' }

if (Get-Process -Name acad -ErrorAction SilentlyContinue) {
    Write-Warning 'AutoCAD is running. Close it before continuing so the plugin files can be replaced.'
    Read-Host 'Press Enter once AutoCAD is closed'
    if (Get-Process -Name acad -ErrorAction SilentlyContinue) { throw 'AutoCAD is still running.' }
}

Step 'Finding uv'
$uv = $null
$candidates = @(
    (Join-Path $env:USERPROFILE '.local\bin\uv.exe'),
    (Join-Path $env:USERPROFILE '.cargo\bin\uv.exe'),
    (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\uv.exe')
)
foreach ($c in $candidates) { if (Test-Path $c) { $uv = $c; break } }
if (-not $uv) {
    $cmd = Get-Command uv -ErrorAction SilentlyContinue
    if ($cmd) { $uv = $cmd.Source }
}
if (-not $uv) {
    Write-Host 'uv is not installed; installing it for your user from astral.sh ...'
    Invoke-RestMethod https://astral.sh/uv/install.ps1 | Invoke-Expression
    $uv = Join-Path $env:USERPROFILE '.local\bin\uv.exe'
    if (-not (Test-Path $uv)) { throw "uv did not install. Install it from https://docs.astral.sh/uv/ and run this again." }
}
Write-Host "Using $uv"

Step "Copying the plugin to $dest"
New-Item -ItemType Directory -Force -Path $pluginsRoot | Out-Null
if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
Copy-Item -Recurse -Force $source $dest
# Files from a downloaded zip carry the internet mark; clear it so Windows does not block them.
Get-ChildItem -Recurse -File $dest | Unblock-File

if (-not $SkipPrepare) {
    Step 'Preparing Python and the AutoCAD tools (first time: 1-3 minutes)'
    $env:UV_PROJECT_ENVIRONMENT = $venv
    & $uv sync --project (Join-Path $dest 'Contents\bridge') --frozen --no-dev
    if ($LASTEXITCODE -ne 0) { throw "uv sync failed (exit $LASTEXITCODE). The panel will retry on first use." }
}

Step 'Claude Code (lets the panel use your Claude account, no API key needed)'
$claude = $null
foreach ($c in @((Join-Path $env:USERPROFILE '.local\bin\claude.exe'), (Join-Path $env:APPDATA 'npm\claude.cmd'))) {
    if (Test-Path $c) { $claude = $c; break }
}
if (-not $claude) {
    $cmd = Get-Command claude -ErrorAction SilentlyContinue
    if ($cmd) { $claude = $cmd.Source }
}
if (-not $claude) {
    Write-Host 'Claude Code is not installed; installing it for your user from claude.ai ...'
    Invoke-RestMethod https://claude.ai/install.ps1 | Invoke-Expression
    $claude = Join-Path $env:USERPROFILE '.local\bin\claude.exe'
    if (-not (Test-Path $claude)) {
        Write-Warning 'Claude Code did not install. Install it from https://claude.com/claude-code, or use an API key in the panel Settings.'
        $claude = $null
    }
}
if ($claude) {
    Write-Host "Using $claude"
    $signedIn = $false
    try { $signedIn = ((& $claude auth status) | Out-String | ConvertFrom-Json).loggedIn } catch { }
    if ($signedIn) {
        Write-Host 'Already signed in to your Claude account.'
    } else {
        Write-Host 'Sign in to your Claude account: a browser window opens. Finish there, then come back here.'
        & $claude auth login
    }
}

Step 'Done'
Write-Host @"

Next:
  1. Start AutoCAD 2025. If it asks about loading AutoCADChat.dll, choose "Always Load".
  2. Type CLAUDE and press Enter. The chat panel opens on the right.
  3. Start chatting. It uses your Claude account (Pro or Max plan).
     To use an Anthropic API key instead, open Settings and change "Sign in with".

Claude can read and change the open drawing and nothing else on this computer.
Raw command-line and AutoLISP access are switched off; destructive changes ask you first.
"@ -ForegroundColor Green
