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

## Privacy
Party members are written as `p1`..`p7` with their job, never by name. Enemy names are kept.
Nothing is uploaded; files stay on your PC until you share them.

Requires nothing else. Works for any job.
