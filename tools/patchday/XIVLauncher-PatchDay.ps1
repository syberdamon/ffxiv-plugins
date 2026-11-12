<#
Patch-day launcher for XIVLauncher (use instead of the normal XIVLauncher shortcut).

Every launch:
  1. Your game version (game\ffxivgame.ver) vs the game version the official Dalamud supports
     (asked from kamori, the same place XIVLauncher asks).
  2. Supported -> start XIVLauncher normally, and remove any temporary Dalamud copy.
  3. Not supported yet -> read the patch-day check (patch-status.json, published every 30 min by
     syberdamon/ffxiv-plugins' GitHub Actions). If it says this game version is safe for the
     current Dalamud, ask once per game version; on Yes, start XIVLauncher with
     --dalamud-runner-override pointing at a copy of the official Dalamud (the launcher's version
     check is skipped). Unsafe / still settling / no answer: start normally (no Dalamud) and say why.
  4. Crash fallback: if the game crashed (dalamud_appcrash_*) within 10 min of a temporary launch,
     that game version is marked failed and the temporary Dalamud isn't used again for it.
As soon as goatcorp releases the official update, the next launch is back on it (step 2).
Log: %AppData%\XIVLauncher\patchday\patchday.log
#>
param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $PassThrough)

$ErrorActionPreference = 'Stop'
# Test hooks (CI): PATCHDAY_TEST=1 replaces the dialogs, the network and the launch.
$Test = $env:PATCHDAY_TEST -eq '1'
if (-not $Test) { Add-Type -AssemblyName System.Windows.Forms | Out-Null }

$StatusUrl = 'https://raw.githubusercontent.com/syberdamon/ffxiv-plugins/patch-status/patch-status.json'
$Roaming   = Join-Path $env:APPDATA 'XIVLauncher'
$Home_     = Join-Path $Roaming 'patchday'
$StateFile = Join-Path $Home_ 'state.json'
$LogFile   = Join-Path $Home_ 'patchday.log'
$TempRoot  = Join-Path $Roaming 'addon\PatchDayDalamud'   # outside addon\Hooks: the launcher cleans that up
New-Item -ItemType Directory -Force -Path $Home_ | Out-Null

function Log([string] $msg) {
    Add-Content -Path $LogFile -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $msg)
}

function Show([string] $msg, [string] $buttons = 'OK', [string] $icon = 'Information') {
    if ($Test) { Write-Host "DIALOG[$buttons]: $($msg -replace "`n", ' | ')"; return $(if ($env:PATCHDAY_ANSWER) { $env:PATCHDAY_ANSWER } else { 'OK' }) }
    [System.Windows.Forms.MessageBox]::Show($msg, 'XIVLauncher patch-day', $buttons, $icon)
}

function Get-Json([string] $url, [string] $testFile) {
    if ($Test) { if ($testFile -and (Test-Path $testFile)) { return Get-Content $testFile -Raw | ConvertFrom-Json } else { throw "offline (test)" } }
    return Invoke-RestMethod -TimeoutSec 15 -Uri $url -Headers @{ 'Cache-Control' = 'no-cache' }
}

function Load-State {
    if (Test-Path $StateFile) {
        try { return Get-Content $StateFile -Raw | ConvertFrom-Json } catch { }
    }
    return [pscustomobject]@{ approved = [pscustomobject]@{}; failed = [pscustomobject]@{}; lastTempLaunch = $null; lastTempGame = $null }
}

function Save-State($s) { $s | ConvertTo-Json -Depth 5 | Set-Content -Path $StateFile -Encoding UTF8 }

function Get-Prop($obj, [string] $name) {
    if ($null -eq $obj) { return $null }
    $p = $obj.PSObject.Properties | Where-Object { $_.Name -ieq $name } | Select-Object -First 1
    if ($p) { return $p.Value } else { return $null }
}

function Set-Top($state, [string] $name, $value) {
    $state | Add-Member -Force -NotePropertyName $name -NotePropertyValue $value
}

function Set-Flag($state, [string] $bag, [string] $key, $value) {
    $table = Get-Prop $state $bag
    if ($null -eq $table -or $table -is [string]) { $table = [pscustomobject]@{} ; $state | Add-Member -Force -NotePropertyName $bag -NotePropertyValue $table }
    $table | Add-Member -Force -NotePropertyName $key -NotePropertyValue $value
}

function Find-Launcher {
    if ($Test) { return 'XIVLauncher.exe' }
    foreach ($p in @((Join-Path $env:LOCALAPPDATA 'XIVLauncher\current\XIVLauncher.exe'),
                     (Join-Path $env:LOCALAPPDATA 'XIVLauncher\XIVLauncher.exe'))) {
        if (Test-Path $p) { return $p }
    }
    throw 'XIVLauncher.exe not found under %LOCALAPPDATA%\XIVLauncher.'
}

function Start-XL([string[]] $extra) {
    $exe = Find-Launcher
    $all = @()
    if ($PassThrough) { $all += $PassThrough }
    if ($extra) { $all += $extra }
    Log ("Starting XIVLauncher {0}" -f ($all -join ' '))
    if ($Test) { Write-Host ("LAUNCH: {0}" -f ($all -join ' ')); return }
    if ($all.Count -gt 0) { Start-Process -FilePath $exe -ArgumentList $all } else { Start-Process -FilePath $exe }
}

try {
    $state  = Load-State
    $config = Get-Content (Join-Path $Roaming 'launcherConfigV3.json') -Raw | ConvertFrom-Json
    $game   = (Get-Content (Join-Path $config.GamePath 'game\ffxivgame.ver') -Raw).Trim()
    $bucket = if ($config.DalamudRolloutBucket) { $config.DalamudRolloutBucket } else { 'Control' }

    # Crash fallback: did the last temporary launch crash soon after starting?
    $lastTemp = Get-Prop $state 'lastTempLaunch'
    $lastGame = Get-Prop $state 'lastTempGame'
    if ($lastTemp -and $lastGame) {
        $since = [datetime]::Parse($lastTemp)
        $crash = Get-ChildItem -Path $Roaming -Filter 'dalamud_appcrash_*.log' -ErrorAction SilentlyContinue |
                 Where-Object { $_.LastWriteTime -ge $since -and $_.LastWriteTime -le $since.AddMinutes(10) } |
                 Select-Object -First 1
        if ($crash) {
            Set-Flag $state 'failed' $lastGame $true
            Log "Temporary Dalamud crashed for $lastGame ($($crash.Name)); not using it again for this game version."
        }
        Set-Top $state 'lastTempLaunch' $null; Set-Top $state 'lastTempGame' $null
        Save-State $state
    }

    try {
        $official = Get-Json "https://kamori.goats.dev/Dalamud/Release/VersionInfo?track=release&bucket=$bucket" $env:PATCHDAY_OFFICIAL
    } catch {
        Log "Couldn't reach kamori ($($_.Exception.Message)); starting normally."
        Start-XL @(); exit
    }
    $offVer  = Get-Prop $official 'AssemblyVersion'
    $offGame = Get-Prop $official 'SupportedGameVer'

    if ($offGame -eq $game) {
        if (Test-Path $TempRoot) {
            Remove-Item -Recurse -Force $TempRoot -ErrorAction SilentlyContinue
            Log "Official Dalamud $offVer supports ${game}: back on official, temporary copy removed."
        }
        Start-XL @(); exit
    }

    Log "Game $game, official Dalamud $offVer supports $offGame."
    if (Get-Prop (Get-Prop $state 'failed') $game) {
        Show "Game $game isn't supported by Dalamud yet, and the temporary Dalamud crashed earlier for this version.`n`nStarting without Dalamud. It'll be back automatically once the official update is out." 'OK' 'Warning' | Out-Null
        Start-XL @(); exit
    }

    try {
        $status = Get-Json $StatusUrl $env:PATCHDAY_STATUS
    } catch { $status = $null }

    $sGame = Get-Prop $status 'latestGame'; $sDal = Get-Prop $status 'officialDalamud'; $safe = Get-Prop $status 'safe'
    if (-not $status -or $sGame -ne $game -or $sDal -ne $offVer -or -not $safe) {
        $why = if (-not $status) { 'The patch-day check has no answer yet.' }
               elseif ($sGame -ne $game -or $sDal -ne $offVer) { "The patch-day check hasn't looked at game $game with Dalamud $offVer yet." }
               else { Get-Prop $status 'summary' }
        Log "Not safe to run the temporary Dalamud: $why"
        Show "Game $game isn't supported by Dalamud $offVer yet.`n`n$why`n`nStarting without Dalamud. Next launch checks again; you'll be back on Dalamud as soon as it's safe or official." 'OK' 'Information' | Out-Null
        Start-XL @(); exit
    }

    # Safe: ask once per game version.
    $ok = Get-Prop (Get-Prop $state 'approved') $game
    if ($null -eq $ok) {
        $answer = Show ("Temporary Dalamud for game $game?`n`n" + (Get-Prop $status 'summary') +
                        "`n`nThis runs the current official Dalamud ($offVer) on the new game version until goatcorp's update is out, then switches back automatically. Other plugins may still need their own updates.`n`nUse it?") 'YesNo' 'Question'
        $ok = ($answer -eq 'Yes')
        Set-Flag $state 'approved' $game $ok
        Save-State $state
        Log "Asked about game ${game}: $(if ($ok) { 'yes' } else { 'no' })."
    }
    if (-not $ok) { Start-XL @(); exit }

    # Copy the official Dalamud out of addon\Hooks (the launcher deletes other folders there).
    $src = Join-Path $Roaming "addon\Hooks\$offVer"
    if (-not (Test-Path (Join-Path $src 'Dalamud.Injector.exe'))) { $src = Join-Path $Roaming 'addon\Hooks\dev' }
    $dst = Join-Path $TempRoot $offVer
    if (-not (Test-Path (Join-Path $dst 'Dalamud.Injector.exe'))) {
        if (Test-Path $TempRoot) { Remove-Item -Recurse -Force $TempRoot }
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        Copy-Item -Recurse -Force (Join-Path $src '*') $dst
        Log "Copied $src to $dst."
    }
    Set-Top $state 'lastTempLaunch' ((Get-Date).ToString('o')); Set-Top $state 'lastTempGame' $game
    Save-State $state
    Start-XL @('--dalamud-runner-override', (Join-Path $dst 'Dalamud.Injector.exe'))
}
catch {
    Log "Error: $($_.Exception.Message). Starting normally."
    try { Start-XL @() } catch { Show "Couldn't start XIVLauncher: $($_.Exception.Message)" 'OK' 'Error' | Out-Null }
}
