<#
.SYNOPSIS
    Installs BepInEx and the console plugin from scratch, then verifies it loaded.

.DESCRIPTION
    Everything a new player needs, with nothing to download by hand: finds the game through
    Steam, fetches the BepInEx build the plugin was compiled against, fetches the plugin from
    the repository's latest release, starts the game and reads the log to confirm the plugin
    actually loaded.

    Safe to re-run. An existing BepInEx is left alone unless -Force is given, which matters
    because reinstalling it would discard the interop assemblies and force another slow
    first-run generation.

.EXAMPLE
    .\install.ps1
    Installs everything and launches the game.

.EXAMPLE
    .\install.ps1 -GameDir 'D:\Games\Nivalis Nights'
    For a game Steam cannot be asked about, or a non-Steam copy.

.EXAMPLE
    .\install.ps1 -SkipLaunch
    Installs without starting the game, so interop generation happens whenever you play next.
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [int]$AppId = 1488490,
    [switch]$SkipLaunch,
    [switch]$Force,
    # Minutes to wait for the plugin to appear in the log. The first launch has to generate the
    # interop assemblies before any plugin loads, and that is the slow part.
    [int]$TimeoutMinutes = 12
)

$ErrorActionPreference = 'Stop'

# The plugin is built against this exact BepInEx build, so the version is pinned rather than
# resolved to "newest": the IL2CPP line is a prerelease whose interop API still moves, and a
# mismatch shows up as a plugin that silently fails to load.
$bepinexVersion = '6.0.0-be.697'
$bepinexUrl = 'https://builds.bepinex.dev/projects/bepinex_be/697/' +
              'BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.697%2B5362580.zip'
$pluginRepo = 'Vesper0802/Nivalis_console'
$pluginName = 'NivalisDevUnlock.dll'

# Windows PowerShell 5.1 still defaults to TLS 1.0, which GitHub refuses.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Say($text, $colour = 'Gray') { Write-Host $text -ForegroundColor $colour }
function Step($text) { Write-Host "`n$text" -ForegroundColor Cyan }

# ---------------------------------------------------------------------------------------------
# 1. Find the game.
# ---------------------------------------------------------------------------------------------

function Find-SteamRoot {
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        $prop = Get-ItemProperty $key -ErrorAction SilentlyContinue
        # The two keys spell the value differently, and SteamPath comes back with forward slashes.
        $path = if ($prop.SteamPath) { $prop.SteamPath } else { $prop.InstallPath }
        if ($path) {
            $path = $path -replace '/', '\'
            if (Test-Path $path) { return $path }
        }
    }
    return $null
}

function Find-GameDir($steamRoot, $appId) {
    # A Steam install can be spread over several drives, so the install is looked for in every
    # library rather than assumed to be under the Steam root.
    $libraries = @($steamRoot)
    $vdf = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($match in Select-String -Path $vdf -Pattern '"path"\s+"(.+?)"' -AllMatches) {
            foreach ($m in $match.Matches) { $libraries += $m.Groups[1].Value -replace '\\\\', '\' }
        }
    }

    foreach ($library in $libraries | Select-Object -Unique) {
        $manifest = Join-Path $library "steamapps\appmanifest_$appId.acf"
        if (-not (Test-Path $manifest)) { continue }

        $installDir = (Select-String -Path $manifest -Pattern '"installdir"\s+"(.+?)"' |
                       Select-Object -First 1).Matches.Groups[1].Value
        if (-not $installDir) { continue }

        $candidate = Join-Path $library "steamapps\common\$installDir"
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

Step 'Locating Nivalis Nights...'
if ($GameDir) {
    if (-not (Test-Path $GameDir)) { throw "No such folder: $GameDir" }
} else {
    $steamRoot = Find-SteamRoot
    if (-not $steamRoot) {
        throw 'Steam was not found in the registry. Pass the game folder with -GameDir instead.'
    }
    Say "  Steam: $steamRoot" DarkGray
    $GameDir = Find-GameDir $steamRoot $AppId
    if (-not $GameDir) {
        throw "Steam has no install of app $AppId. Pass the game folder with -GameDir instead."
    }
}

# The executable is checked rather than just the folder, because an uninstall can leave the
# folder behind and installing into an empty one would look like it worked.
$exe = Get-ChildItem $GameDir -Filter '*.exe' -ErrorAction SilentlyContinue |
       Where-Object { $_.Name -notmatch 'UnityCrashHandler' } | Select-Object -First 1
if (-not $exe) { throw "No game executable in $GameDir — is that the right folder?" }
Say "  Game:  $GameDir" Green

$bepinexDir = Join-Path $GameDir 'BepInEx'
$pluginDir = Join-Path $bepinexDir 'plugins'
$log = Join-Path $bepinexDir 'LogOutput.log'

# ---------------------------------------------------------------------------------------------
# 2. The game must not be running: BepInEx is only read at startup, and a running game holds
#    the plugin file open so the copy would fail.
# ---------------------------------------------------------------------------------------------

# A process that has closed can linger with no threads, no handles and almost no memory, and
# those husks are not something the player can act on, so they must not be reported as a
# running game. They do still matter below: a husk keeps its loaded DLLs locked.
$running = Get-Process -Name 'Nivalis Nights' -ErrorAction SilentlyContinue |
           Where-Object { -not $_.HasExited -and $_.Threads.Count -gt 0 }
if ($running) {
    throw "The game is running (pid $($running.Id -join ', ')). Close it and run this again."
}

# ---------------------------------------------------------------------------------------------
# 3. BepInEx.
# ---------------------------------------------------------------------------------------------

Step 'Installing BepInEx...'
$doorstop = Join-Path $GameDir 'winhttp.dll'
$alreadyInstalled = (Test-Path $doorstop) -and (Test-Path (Join-Path $bepinexDir 'core'))

if ($alreadyInstalled -and -not $Force) {
    $installed = (Get-Item (Join-Path $bepinexDir 'core\BepInEx.Core.dll')).VersionInfo.ProductVersion
    Say "  Already installed: $installed" DarkGray
    Say '  Keeping it, so the interop assemblies survive. Use -Force to reinstall.' DarkGray
} else {
    $zip = Join-Path $env:TEMP "BepInEx-IL2CPP-$bepinexVersion.zip"
    if (Test-Path $zip) {
        Say "  Using the copy already in TEMP." DarkGray
    } else {
        Say "  Downloading $bepinexVersion (about 33 MB, includes its own .NET runtime)..."
        Invoke-WebRequest -Uri $bepinexUrl -OutFile $zip -UseBasicParsing
    }

    Say '  Extracting...'
    # The archive holds the layout the game folder needs, so it unpacks over the top.
    Expand-Archive -Path $zip -DestinationPath $GameDir -Force
    Say "  BepInEx $bepinexVersion installed." Green
}

# ---------------------------------------------------------------------------------------------
# 4. The plugin, from the repository's latest release.
# ---------------------------------------------------------------------------------------------

Step 'Installing the console plugin...'
$releaseApi = "https://api.github.com/repos/$pluginRepo/releases/latest"
try {
    $release = Invoke-RestMethod -Uri $releaseApi -Headers @{ 'User-Agent' = 'nivalis-installer' } `
                                 -UseBasicParsing
} catch {
    throw "Could not reach the release list ($releaseApi): $($_.Exception.Message)"
}

$asset = $release.assets | Where-Object { $_.name -eq $pluginName } | Select-Object -First 1
if (-not $asset) {
    throw "Release $($release.tag_name) has no $pluginName attached. " +
          "Build it yourself instead — see the README."
}

New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
$target = Join-Path $pluginDir $pluginName

# A previous copy can still be locked even with the game closed, because a husk holds its
# loaded DLLs as mapped images rather than as handles — nothing is left to close and only a
# reboot clears it. The name can still be freed by renaming, which is enough to install over.
if (Test-Path $target) {
    try {
        $handle = [IO.File]::Open($target, 'Open', 'ReadWrite', 'None')
        $handle.Close()
    } catch {
        $aside = "$pluginName.locked-$(Get-Date -Format HHmmss)"
        Rename-Item $target $aside
        Say "  The old plugin was locked by a closed-but-lingering game; moved it aside." DarkYellow
    }
}

# Sweep earlier leftovers now that Windows may have released them. BepInEx only loads *.dll,
# so these were never active, just untidy.
Get-ChildItem $pluginDir -Filter "$pluginName.locked-*" -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }

Say "  Downloading $pluginName from $($release.tag_name)..."
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $target -UseBasicParsing
Say "  Installed $((Get-Item $target).Length) bytes to BepInEx\plugins." Green

# ---------------------------------------------------------------------------------------------
# 5. Launch, and prove it loaded rather than claiming it did.
# ---------------------------------------------------------------------------------------------

if ($SkipLaunch) {
    Step 'Done. Start the game and press F1 after loading a save.'
    Say "  The first launch generates interop assemblies and takes several minutes." DarkGray
    return
}

# Starting from no log means a marker found below has to have come from this run.
if (Test-Path $log) { Remove-Item $log -Force -ErrorAction SilentlyContinue }

Step 'Starting the game...'
Start-Process "steam://rungameid/$AppId"

Say "  Waiting for the plugin to load (up to $TimeoutMinutes minutes)." DarkGray
Say '  A first run has to generate the interop assemblies, which is the slow part.' DarkGray

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$reportedInterop = $false
$loaded = $false

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    if (-not (Test-Path $log)) { continue }

    # The log is read with sharing, because BepInEx holds it open for writing.
    try {
        $stream = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
        $text = (New-Object IO.StreamReader($stream)).ReadToEnd()
        $stream.Close()
    } catch { continue }

    if (-not $reportedInterop -and $text -match 'Il2CppInterop|Generating') {
        Say '  BepInEx is up; generating interop assemblies...' DarkGray
        $reportedInterop = $true
    }

    if ($text -match 'Loading \[Nivalis Nights Dev Unlock') {
        $loaded = $true
        break
    }

    if ($text -match 'Failed to generate Il2Cpp interop assemblies') {
        throw "BepInEx could not generate the interop assemblies. See $log"
    }
}

if ($loaded) {
    Step 'Installed and loaded.'
    Say '  Load a save, then press F1. Type help for the command list.' Green
} else {
    Step 'The plugin has not appeared in the log yet.'
    Say "  That is not necessarily a failure — a slow first run can outlast the wait." Yellow
    Say "  Check it with: Get-Content '$log' -Wait -Tail 40" DarkGray
}
