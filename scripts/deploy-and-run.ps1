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
# A process that has already begun exiting still shows up by name but refuses Stop-Process,
# so failures are tolerated here and only the liveness check below decides whether to abort.
function Get-GameProcesses {
    Get-Process -Name 'Nivalis Nights' -ErrorAction SilentlyContinue |
        Where-Object { -not $_.HasExited }
}

$procs = Get-GameProcesses
if ($procs) {
    Write-Host "Closing game (pid $($procs.Id -join ', '))..." -ForegroundColor Yellow
    foreach ($proc in $procs) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop }
        catch { Write-Host "  pid $($proc.Id) is already exiting." -ForegroundColor DarkGray }
    }

    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 500
        if (-not (Get-GameProcesses)) { break }
    }

    $stuck = Get-GameProcesses
    if ($stuck) {
        # A husk reports no threads, no handles and no working set, yet still keeps the DLLs it
        # had loaded locked: a mapped image is a section reference rather than a handle, and
        # something in the kernel is holding the dying process together. Stop-Process has
        # nothing left to kill, so only a reboot clears them. The rename below steps around the
        # one that matters here.
        Write-Host "pid $($stuck.Id -join ', ') is wedged in termination; continuing." -ForegroundColor Yellow
        if ($stuck.Count -ge 5) {
            # These also hold interop\Assembly-CSharp.dll, which has to be rewritten from
            # scratch after a game update. That is the one job they will block outright.
            Write-Host "  $($stuck.Count) husks have built up. Reboot before regenerating interop." -ForegroundColor DarkYellow
        }
    } else {
        Write-Host 'Game closed.' -ForegroundColor Green
    }
} else {
    Write-Host 'Game not running.' -ForegroundColor DarkGray
}

# A wedged process keeps its handle on the old plugin, and a locked file cannot be
# overwritten — but it can still be renamed out of the way, which frees the name.
$plugin = Join-Path $GameDir 'BepInEx\plugins\NivalisDevUnlock.dll'
if (Test-Path $plugin) {
    try {
        $handle = [IO.File]::Open($plugin, 'Open', 'ReadWrite', 'None')
        $handle.Close()
    } catch {
        $aside = "NivalisDevUnlock.dll.locked-$(Get-Date -Format HHmmss)"
        Rename-Item $plugin $aside
        Write-Host "Old plugin was locked; moved it to $aside." -ForegroundColor Yellow
    }
}

# Sweep away earlier husks' leftovers once Windows has released them.
Get-ChildItem (Split-Path $plugin -Parent) -Filter '*.dll.locked-*' -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }

# 2. Build and deploy.
Write-Host 'Building...' -ForegroundColor Cyan
dotnet build "$repo\src\NivalisDevUnlock" -c Release -p:Deploy=true -p:GameDir=$GameDir
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

if (-not (Test-Path $plugin)) { throw "Plugin missing after deploy: $plugin" }
Write-Host "Deployed $((Get-Item $plugin).Length) bytes at $((Get-Item $plugin).LastWriteTime)." -ForegroundColor Green

# 3. Start from a clean log so the next read cannot show a stale run.
$log = Join-Path $GameDir 'BepInEx\LogOutput.log'
if (-not $KeepLog -and (Test-Path $log)) {
    # Windows can hold the handle for a moment after the process exits, and a stale log is
    # not worth aborting a successful deploy over.
    try {
        Remove-Item $log -Force -ErrorAction Stop
        Write-Host 'Cleared LogOutput.log.' -ForegroundColor DarkGray
    } catch {
        Write-Host 'Log still locked; it will be overwritten at startup.' -ForegroundColor DarkGray
    }
}

# 4. Launch.
if ($NoLaunch) {
    Write-Host 'Skipping launch (-NoLaunch).' -ForegroundColor DarkGray
    return
}
Write-Host 'Launching via Steam...' -ForegroundColor Cyan
Start-Process "steam://rungameid/$AppId"
Write-Host "Watch the log with: Get-Content '$log' -Wait -Tail 40" -ForegroundColor DarkGray
