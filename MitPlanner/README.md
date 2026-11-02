# Mit Planner

Boss timelines with countdowns, plus a per-job plan of which cooldowns to use on
which mechanics.

Commands: `/mitplan` (settings), `/mitplan preview`, `/mitplan stop`, `/mitplan timeline`.

## How it works
- **Fight detection:** entering a supported duty loads its timeline
  (bundled: **The Cinder Drift**, zone 897).
- **Clock:** starts on the pull and syncs to the boss's real abilities using the
  fight's cactbot timeline, so it stays accurate and follows phase changes.
- **Timeline window:** next 5 mechanics (configurable) with countdowns, type
  badges (TB = tank buster, AOE = raid-wide, STK = stack, MECH = mechanic), and
  icons of the cooldowns you planned for each.
- **Plan** (`/mitplan` > Plan): for each mechanic, add one or more cooldowns,
  each with its own lead time (default 3 s before it lands). Repeated mechanics
  are numbered (Stamp #1, #2, #3) so each hit can have a different answer.
  Plans are saved per fight and per job.
- **Callouts:** a few seconds before each press time: "Rampart in 2.0 -> Stamp",
  then "USE Rampart NOW", then "(done)" once you've used it. If the cooldown
  isn't back yet you get a red "still on cooldown!" warning.
- **Auto-use (optional):** tick "Auto" on a plan entry and turn on "Allow
  auto-use" under Display; the plugin then presses it at the planned time.
  Off by default.

## Testing without a duty
Display > **Preview** runs the timeline from 0 anywhere (no syncing, no auto-use),
so you can check plans and callout placement before queueing.

## Cinder Drift default tags (from cactbot's triggers)
- Tank busters: Stamp (x3), Ruby Claw (x2)
- Raid-wides: Optimized Ultima, Outrage, Optimized Ultima Enrage
- Stack: High-Powered Homing Lasers
- Everything is retaggable in the Plan tab.

## Known limits
- One bundled fight for now; other cactbot fights can be added the same way.
- Accuracy depends on the cactbot timeline, which has a couple of FIXME notes
  for this fight. If the clock drifts, the Status tab shows the last sync.

Timelines and default tags from cactbot (Apache-2.0); see
[THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md).
