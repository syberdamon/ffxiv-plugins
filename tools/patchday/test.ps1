# Scenario test for XIVLauncher-PatchDay.ps1 (run in CI on Windows PowerShell 5.1, like the PC).
# Builds a fake %APPDATA%\XIVLauncher and game folder, then walks the patch-day paths.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'XIVLauncher-PatchDay.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ("patchday-test-" + [guid]::NewGuid().ToString('N'))
$env:APPDATA = Join-Path $root 'Roaming'; $env:LOCALAPPDATA = Join-Path $root 'Local'
$xl = Join-Path $env:APPDATA 'XIVLauncher'; $gameRoot = Join-Path $root 'Game'
New-Item -ItemType Directory -Force -Path (Join-Path $xl 'addon\Hooks\15.0.3.9'), (Join-Path $gameRoot 'game') | Out-Null
Set-Content (Join-Path $xl 'addon\Hooks\15.0.3.9\Dalamud.Injector.exe') 'fake'
@{ GamePath = $gameRoot; DalamudRolloutBucket = 'Control' } | ConvertTo-Json | Set-Content (Join-Path $xl 'launcherConfigV3.json')
Set-Content (Join-Path $gameRoot 'game\ffxivgame.ver') '2026.11.03.0000.0000'
$off = Join-Path $root 'official.json'; $st = Join-Path $root 'status.json'
$env:PATCHDAY_TEST = '1'; $env:PATCHDAY_OFFICIAL = $off; $env:PATCHDAY_STATUS = $st
$fails = 0

function Run([string] $name, [string] $answer, [string[]] $mustHave, [string[]] $mustNot) {
    $env:PATCHDAY_ANSWER = $answer
    $out = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script *>&1 | Out-String)
    $ok = $true
    # Plain substring match: -like would read "[OK]" as a wildcard character class.
    foreach ($m in $mustHave) { if (-not $out.Contains($m)) { $ok = $false; Write-Host "  missing: $m" } }
    foreach ($m in $mustNot)  { if ($out.Contains($m))      { $ok = $false; Write-Host "  unexpected: $m" } }
    Write-Host ("{0} {1}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name)
    if (-not $ok) { Write-Host $out; $script:fails++ }
}
function Official([string] $ver, [string] $game) { @{ AssemblyVersion = $ver; SupportedGameVer = $game } | ConvertTo-Json | Set-Content $off }
function Status([string] $state, [bool] $safe) {
    @{ latestGame = '2026.11.03.0000.0000'; officialDalamud = '15.0.3.9'; state = $state; safe = $safe; summary = "test: $state" } | ConvertTo-Json | Set-Content $st
}

Official '15.0.3.9' '2026.10.16.0000.0000'
Run 'offline status, not yet supported -> normal launch' '' @('LAUNCH:') @('dalamud-runner-override')
Status 'settling' $false
Run 'settling -> normal launch, says why' '' @('DIALOG[OK]', 'test: settling', 'LAUNCH:') @('dalamud-runner-override')
Status 'safe' $true
Run 'safe, user says No -> normal launch' 'No' @('DIALOG[YesNo]', 'LAUNCH:') @('dalamud-runner-override')
Run 'safe, already answered No -> no question' '' @('LAUNCH:') @('DIALOG[YesNo]', 'dalamud-runner-override')
Remove-Item (Join-Path $xl 'patchday\state.json')
Run 'safe, user says Yes -> temporary Dalamud' 'Yes' @('DIALOG[YesNo]', 'dalamud-runner-override', 'PatchDayDalamud\15.0.3.9\Dalamud.Injector.exe') @()
if (-not (Test-Path (Join-Path $xl 'addon\PatchDayDalamud\15.0.3.9\Dalamud.Injector.exe'))) { Write-Host 'FAIL copy missing'; $fails++ } else { Write-Host 'PASS copy made' }
Run 'next launch -> temporary again, no question' '' @('dalamud-runner-override') @('DIALOG[YesNo]')
Start-Sleep -Seconds 1
Set-Content (Join-Path $xl 'dalamud_appcrash_test.log') 'crash'
Run 'crash right after a temporary launch -> marked failed, normal launch' '' @('crashed earlier', 'LAUNCH:') @('dalamud-runner-override')
Run 'still failed for this game version' '' @('crashed earlier') @('dalamud-runner-override')
Official '15.0.4.0' '2026.11.03.0000.0000'
Run 'official update out -> back on official' '' @('LAUNCH:') @('dalamud-runner-override', 'DIALOG')
if (Test-Path (Join-Path $xl 'addon\PatchDayDalamud')) { Write-Host 'FAIL temporary copy not removed'; $fails++ } else { Write-Host 'PASS temporary copy removed' }
Write-Host "--- log ---"; Get-Content (Join-Path $xl 'patchday\patchday.log')
Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
if ($fails) { throw "$fails failure(s)" }
