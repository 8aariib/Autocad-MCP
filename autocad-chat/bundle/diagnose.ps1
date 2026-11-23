<#
  Claude for AutoCAD - diagnostics.
      powershell -ExecutionPolicy Bypass -File .\diagnose.ps1
  Checks every piece and writes claude-autocad-report.txt to your Desktop.
  It changes nothing. The report contains no API key or password.
#>
$ErrorActionPreference = 'Continue'
$report = Join-Path ([Environment]::GetFolderPath('Desktop')) 'claude-autocad-report.txt'
$lines = New-Object System.Collections.Generic.List[string]
function Say($t) { $lines.Add($t); Write-Host $t }
function Check($name, $ok, $detail) {
    $mark = if ($ok) { '[ OK ]' } else { '[FAIL]' }
    Say ("{0} {1}: {2}" -f $mark, $name, $detail)
}

Say "Claude for AutoCAD diagnostics  $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
Say "Windows $([Environment]::OSVersion.Version)  PowerShell $($PSVersionTable.PSVersion)"
Say ''

# AutoCAD
$acad = Test-Path 'HKLM:\SOFTWARE\Autodesk\AutoCAD\R25.0'
Check 'AutoCAD 2025 installed' $acad 'registry key R25.0'
$running = Get-Process -Name acad -ErrorAction SilentlyContinue
Say ("       AutoCAD running now: {0}" -f [bool]$running)
if ($running) { try { Say ("       acad.exe: {0}" -f $running[0].MainModule.FileVersionInfo.ProductVersion) } catch { } }

# Bundle
$bundle = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\AutoCADChat.bundle'
$dll = Join-Path $bundle 'Contents\AutoCADChat.dll'
Check 'Plugin bundle copied' (Test-Path (Join-Path $bundle 'PackageContents.xml')) $bundle
Check 'Plugin DLL present' (Test-Path $dll) $dll
if (Test-Path $dll) {
    $blocked = Get-Item $dll -Stream Zone.Identifier -ErrorAction SilentlyContinue
    Check 'DLL not blocked by Windows' (-not $blocked) 'internet mark (Zone.Identifier)'
    Say ("       DLL size {0} bytes, modified {1}" -f (Get-Item $dll).Length, (Get-Item $dll).LastWriteTime)
}
Check 'Bridge files present' (Test-Path (Join-Path $bundle 'Contents\bridge\pyproject.toml')) 'Contents\bridge'

# uv and Python
$uv = $null
foreach ($c in @((Join-Path $env:USERPROFILE '.local\bin\uv.exe'), (Join-Path $env:USERPROFILE '.cargo\bin\uv.exe'), (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\uv.exe'))) { if (Test-Path $c) { $uv = $c; break } }
if (-not $uv) { $g = Get-Command uv -ErrorAction SilentlyContinue; if ($g) { $uv = $g.Source } }
Check 'uv installed' ([bool]$uv) "$uv"
$venv = Join-Path $env:LOCALAPPDATA 'AutoCADChat\venv'
$server = Join-Path $venv 'Scripts\autocad-mcp.exe'
Check 'Python environment prepared' (Test-Path $server) $server

# Claude Code
$claude = $null
foreach ($c in @((Join-Path $env:USERPROFILE '.local\bin\claude.exe'), (Join-Path $env:APPDATA 'npm\claude.cmd'))) { if (Test-Path $c) { $claude = $c; break } }
if (-not $claude) { $g = Get-Command claude -ErrorAction SilentlyContinue; if ($g) { $claude = $g.Source } }
Check 'Claude Code installed' ([bool]$claude) "$claude"
if ($claude) {
    try {
        $st = (& $claude auth status 2>$null) | Out-String | ConvertFrom-Json
        Check 'Claude Code signed in' ([bool]$st.loggedIn) ("method: {0}" -f $st.authMethod)
    } catch { Check 'Claude Code signed in' $false "could not read 'claude auth status': $_" }
    try { Say ("       version: {0}" -f ((& $claude --version 2>$null) | Out-String).Trim()) } catch { }
}

# Settings (no key printed)
$settingsPath = Join-Path $env:APPDATA 'AutoCADChat\settings.json'
if (Test-Path $settingsPath) {
    try {
        $s = Get-Content $settingsPath -Raw | ConvertFrom-Json
        Say ("       settings: engine={0} model={1} effort={2} readOnly={3} keySaved={4}" -f $s.Engine, $s.Model, $s.Effort, $s.ReadOnly, [bool]$s.ProtectedApiKey)
    } catch { Say '       settings.json could not be read' }
} else { Say '       settings.json not created yet (the panel has not been opened)' }

# Bridge self-test: start it on the file-only engine and wait for "ready"
if ($uv -and (Test-Path (Join-Path $bundle 'Contents\bridge\pyproject.toml'))) {
    Say ''
    Say 'Starting the chat engine for a self-test (up to 3 minutes the first time)...'
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $uv
    $psi.Arguments = "run --project `"$(Join-Path $bundle 'Contents\bridge')`" --frozen --no-dev autocad-chat-bridge"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables['UV_PROJECT_ENVIRONMENT'] = $venv
    $psi.EnvironmentVariables['UV_NO_PROGRESS'] = '1'
    $psi.EnvironmentVariables['PYTHONUTF8'] = '1'
    $psi.EnvironmentVariables['ACADCHAT_ENGINE'] = 'claude-code'
    $psi.EnvironmentVariables['ACADCHAT_BACKEND'] = 'ezdxf'
    if ($claude) { $psi.EnvironmentVariables['ACADCHAT_CLAUDE_CMD'] = (ConvertTo-Json @($claude) -Compress) }
    $p = [System.Diagnostics.Process]::Start($psi)
    $errTask = $p.StandardError.ReadToEndAsync()
    $deadline = (Get-Date).AddMinutes(3)
    $result = 'no answer'
    while ((Get-Date) -lt $deadline) {
        $task = $p.StandardOutput.ReadLineAsync()
        if (-not $task.Wait(180000)) { break }
        $line = $task.Result
        if ($null -eq $line) { $result = "exited with code $($p.ExitCode)"; break }
        Say "       engine: $line"
        if ($line -match '"type":"ready"') { $result = 'ready'; break }
        if ($line -match '"fatal":true') { $result = 'fatal error (see line above)'; break }
    }
    try { $p.StandardInput.WriteLine('{"type":"shutdown"}') } catch { }
    if (-not $p.WaitForExit(5000)) { try { $p.Kill() } catch { } }
    Check 'Chat engine starts' ($result -eq 'ready') $result
    $err = $errTask.Result
    if ($result -ne 'ready' -and $err) {
        Say '       engine errors (last lines):'
        ($err -split "`n" | Where-Object { $_ -match 'rror|raceback|failed|denied|not found' } | Select-Object -Last 15) | ForEach-Object { Say "         $_" }
    }
}

# Claude desktop app connector
Say ''
$cfgs = @((Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'))
$cfgs += Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName 'LocalCache\Roaming\Claude\claude_desktop_config.json' }
foreach ($cfg in $cfgs) {
    if (Test-Path $cfg) {
        $txt = Get-Content $cfg -Raw
        Check 'Claude app has the AutoCAD connector' ($txt -match '"autocad"') $cfg
    }
}

# Logs
foreach ($log in @('panel.log', 'bridge.log')) {
    $path = Join-Path $env:LOCALAPPDATA "AutoCADChat\logs\$log"
    Say ''
    if (Test-Path $path) {
        Say "---- last lines of $log ----"
        Get-Content $path -Tail 40 | ForEach-Object { Say "  $_" }
    } else { Say "---- $log not created yet ----" }
}

[System.IO.File]::WriteAllLines($report, $lines)
Write-Host ''
Write-Host "Report saved to $report - paste its contents into the chat." -ForegroundColor Green
