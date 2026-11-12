# Patch-day launcher

After a game patch, XIVLauncher starts the game **without Dalamud** until goatcorp releases a
Dalamud for the new game version (on 2026-11-10 that took 12 hours, although the fix was 12
signatures in UI code Dalamud doesn't use). This launcher bridges that gap when it's safe, and goes
back to the official Dalamud by itself.

## How it works
- **GitHub, every 30 min** (`.github/workflows/patch-check.yml` → `tools/patch_check.py`): if
  ClientStructs has moved to a game version official Dalamud doesn't support yet, compare the
  changes (signatures, field offsets, struct sizes) with what Dalamud's own code uses. Waits an
  hour after ClientStructs' last change (signatures land after the version bump). Result:
  `patch-status.json` on the `patch-status` branch: `official`, `settling`, `unsafe` or `safe`.
- **Your PC, every launch** (`XIVLauncher-PatchDay.ps1`):
  - official Dalamud supports your game → normal launch (and the temporary copy is deleted);
  - not yet, and the check says `safe` → asks once per game version; on Yes, starts XIVLauncher
    with `--dalamud-runner-override` on a copy of the official Dalamud, which skips the launcher's
    version check;
  - anything else → normal launch (no Dalamud), with a message saying why;
  - if the game crashes within 10 min of a temporary launch, that game version is marked failed and
    the temporary Dalamud isn't used for it again.
- Log: `%AppData%\XIVLauncher\patchday\patchday.log`. Answers and failures:
  `%AppData%\XIVLauncher\patchday\state.json` (delete it to be asked again).

## Install
1. The files live in `%AppData%\XIVLauncher\patchday\` (`XIVLauncher-PatchDay.ps1` and `.cmd`).
2. Right-click your XIVLauncher shortcut → Properties → Target:
   `"%AppData%\XIVLauncher\patchday\XIVLauncher-PatchDay.cmd"` (or make a new shortcut to it).
   Arguments after it are passed to XIVLauncher.

## Limits
- Covers Dalamud's core only. Other plugins (RSR, BossMod, Artisan...) have their own signatures
  and may still need their own updates; Dalamud usually just disables one that fails.
- Dalamud's own signatures (outside ClientStructs) can't be checked without the game's executable;
  the crash fallback covers that.
- While the override is on, the launcher's version check is off: that's the point, and why it's
  only used when the check says `safe` and you said Yes.
