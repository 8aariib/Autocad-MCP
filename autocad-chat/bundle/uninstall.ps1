<#
  Claude for AutoCAD - uninstaller.
      powershell -ExecutionPolicy Bypass -File .\uninstall.ps1          # remove the plugin
      powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -Purge   # also remove settings, saved key, logs and Python env
#>
param([switch]$Purge)

$ErrorActionPreference = 'Stop'
if (Get-Process -Name acad -ErrorAction SilentlyContinue) { throw 'Close AutoCAD first.' }

$dest = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\AutoCADChat.bundle'
if (Test-Path $dest) { Remove-Item -Recurse -Force $dest; Write-Host "Removed $dest" }
else { Write-Host 'The plugin was not installed.' }

if ($Purge) {
    foreach ($d in @((Join-Path $env:APPDATA 'AutoCADChat'), (Join-Path $env:LOCALAPPDATA 'AutoCADChat'))) {
        if (Test-Path $d) { Remove-Item -Recurse -Force $d; Write-Host "Removed $d" }
    }
}
Write-Host 'Done.'
