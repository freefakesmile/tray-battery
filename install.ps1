# 2026-11-11 23:47
# Install / update / uninstall: GitHub release -> %LOCALAPPDATA%\Programs\TrayBattery, autostart, Start menu shortcut

param(
    [switch]$Uninstall,
    [string]$FromFile   # install a local exe instead of downloading the release
)

$ErrorActionPreference = 'Stop'
$repo     = 'freefakesmile/tray-battery'
$dir      = Join-Path $env:LOCALAPPDATA 'Programs\TrayBattery'
$exe      = Join-Path $dir 'TrayBattery.exe'
$runKey   = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'TrayBattery.lnk'

# Stop a running instance so the exe can be replaced
Get-Process TrayBattery -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if ($Uninstall) {
    Remove-ItemProperty $runKey -Name TrayBattery -ErrorAction SilentlyContinue
    Remove-Item $shortcut -ErrorAction SilentlyContinue
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item HKCU:\Software\TrayBattery -Recurse -ErrorAction SilentlyContinue
    'TrayBattery removed.'
    return
}

New-Item -ItemType Directory -Force $dir | Out-Null

if ($FromFile) {
    Copy-Item $FromFile $exe -Force
    $version = 'local'
} else {
    $rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'TrayBattery-installer' }
    $asset = $rel.assets | Where-Object name -eq 'TrayBattery.exe' | Select-Object -First 1
    if (-not $asset) { throw "Release $($rel.tag_name) has no TrayBattery.exe" }
    Invoke-WebRequest $asset.browser_download_url -OutFile $exe -UseBasicParsing
    Unblock-File $exe
    $version = $rel.tag_name
}

# Autostart at sign-in (same key as "Start with Windows" in Settings)
Set-ItemProperty $runKey -Name TrayBattery -Value "`"$exe`""

# Start menu shortcut: to start it again after Exit
$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut($shortcut)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $dir
$lnk.Description = 'Mouse and headset battery on the taskbar'
$lnk.Save()

Start-Process $exe
"TrayBattery $version installed to $dir, autostart on."
