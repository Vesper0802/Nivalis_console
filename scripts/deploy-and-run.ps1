<#
.SYNOPSIS
    Rebuilds the plugin, deploys it, and restarts the game.

.DESCRIPTION
    The game must be closed before deploying: BepInEx only loads plugins during startup,
    and the running process holds a lock on the plugin DLL so the copy would fail anyway.
    This script always closes it first so that step cannot be forgotten.
#>
[CmdletBinding()]
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Nivalis Nights',
    [int]$AppId = 1488490,
    [switch]$NoLaunch,
    [switch]$KeepLog
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

# 1. Close the game.
$procs = Get-Process -Name 'Nivalis Nights' -ErrorAction SilentlyContinue
if ($procs) {
    Write-Host "Closing game (pid $($procs.Id -join ', '))..." -ForegroundColor Yellow
    $procs | Stop-Process -Force
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 500
        if (-not (Get-Process -Name 'Nivalis Nights' -ErrorAction SilentlyContinue)) { break }
    }
    if (Get-Process -Name 'Nivalis Nights' -ErrorAction SilentlyContinue) {
        throw 'Game process would not exit; deploy aborted.'
    }
    Write-Host 'Game closed.' -ForegroundColor Green
} else {
    Write-Host 'Game not running.' -ForegroundColor DarkGray
}

# 2. Build and deploy.
Write-Host 'Building...' -ForegroundColor Cyan
dotnet build "$repo\src\NivalisDevUnlock" -c Release -p:Deploy=true -p:GameDir=$GameDir
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

$plugin = Join-Path $GameDir 'BepInEx\plugins\NivalisDevUnlock.dll'
if (-not (Test-Path $plugin)) { throw "Plugin missing after deploy: $plugin" }
Write-Host "Deployed $((Get-Item $plugin).Length) bytes at $((Get-Item $plugin).LastWriteTime)." -ForegroundColor Green

# 3. Start from a clean log so the next read cannot show a stale run.
$log = Join-Path $GameDir 'BepInEx\LogOutput.log'
if (-not $KeepLog -and (Test-Path $log)) {
    Remove-Item $log -Force
    Write-Host 'Cleared LogOutput.log.' -ForegroundColor DarkGray
}

# 4. Launch.
if ($NoLaunch) {
    Write-Host 'Skipping launch (-NoLaunch).' -ForegroundColor DarkGray
    return
}
Write-Host 'Launching via Steam...' -ForegroundColor Cyan
Start-Process "steam://rungameid/$AppId"
Write-Host "Watch the log with: Get-Content '$log' -Wait -Tail 40" -ForegroundColor DarkGray
