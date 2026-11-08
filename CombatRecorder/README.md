# Combat Recorder

Records your fights to compact local files, as data for reviewing performance and for building
smarter mitigation, rotation and healing helpers later (see `docs/COMBAT_LOGS.md`).

Commands: `/crec` (settings), `/crec mark <note>` (flag this moment, e.g. "downtime here";
put it on a macro), `/crec on|off`, `/crec stop` (close the current file).

## What's recorded
One file per duty (and per zone visit in the open world if enabled), in
`%AppData%\XIVLauncher\pluginConfigs\CombatRecorder\logs\`, JSON lines, gzipped when closed:

- **Every action** you, your party and nearby enemies use, with damage/heal amounts per target
  (crit / direct hit flags), misses, blocks and parries.
- **Casts** (who, what, at whom, cast time).
- **Buffs and debuffs** gained and lost on you, your party and enemies in the fight (stacks,
  duration, who applied it).
- **Snapshots** every 0.5 s in combat: your HP/MP, position, facing, cast and target (target HP,
  distance to its hitbox edge, targetable or not), party HP/MP, enemy HP.
- Target changes, combat start/end, deaths, and your **marks**.
- Names and cooldown data for each action/status the first time it appears, so files can be
  analysed without game data.

## DPS meter
A window with the current fight (or one of the last 10): one row per party member with **DPS, share, damage, crit %,
direct hit %, deaths, HPS and damage taken**; click a row for that player's skills (damage, share, hits, crit/DH %,
max hit; DoT ticks listed by status). Right-click the title line for the **compact view** (one bar per player), past
fights, hiding names, counting everyone around (open world), locking, and settings. `/crec meter` shows or hides it,
`/crec reset` clears it. Works anywhere, recording on or off.

Counted the way ACT does it (and so Cactbot's overlays):
- Damage is every hit (normal, blocked, parried) plus damage-over-time ticks; healing includes heal-over-time ticks and
  overhealing (ACT's "Healed"). Pets, summons and ground effects count for their owner.
- An encounter starts at the first damage between you or your party and an enemy and ends when you leave combat (or
  change zone). DPS = damage / encounter time (first to last hit), the same time for everyone ("ENCDPS").
- DoT ticks name their source when the game sends it; when it doesn't, the tick goes to whoever has that status on the
  target, split evenly if several do. [Estimate] for that case.
- Not included: rDPS/aDPS (FFLogs' crediting of raid buffs). It needs each player's critical hit and direct hit rates,
  which the game doesn't give for other players, so any figure would be a rough guess.

## Privacy
Party members are written as `p1`..`p7` with their job, never by name (the meter shows names on your screen only; it can hide them). Enemy names are kept.
Nothing is uploaded; files stay on your PC until you share them.

Requires nothing else. Works for any job.
