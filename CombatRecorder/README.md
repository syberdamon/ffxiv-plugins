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
A window with the current fight (or one of the last 10): one row per party member with **DPS, rDPS, aDPS, share, damage, crit %,
direct hit %, deaths, HPS and damage taken**; click a row for that player's skills (damage, share, hits, crit/DH %,
max hit; DoT ticks listed by status). The small **Bars** / **Full** button at the top swaps
between this table and the **compact view** (one bar per player, optionally with each player's % share).
Right-click anywhere on the meter for the views, sort order, columns, past fights, hiding names, role colors, counting everyone around (open world), locking, position
mode and settings. `/crec meter` shows or hides it, `/crec reset` clears it. Works anywhere, recording on or off.

**Position mode** (`/crec meter move`, the settings button, or the right-click menu) shows the meter right away, out of
combat too, unlocked and outlined, with sample rows if there's no fight yet. Drag it, resize it, click **Done**.
**Reset position** in settings puts it back at the default spot if it ends up off screen.

Settings (`/crec`):
- **When it shows:** only while fighting, and for how long after a fight (default 10 s); hide during cutscenes.
- **Layout and look:** lock, click-through when locked, compact view, title line on/off, hide names, color by role,
  compact-view % share on/off, highlight your row, sort by damage / healing / damage taken, max rows (you're always shown), text size,
  background opacity.
- **Columns:** turn each full-view column on or off; align numbers left / center / right (default right); reset widths.
- **Sizing:** drag the lines between the full view's column headers to resize columns (saved). Both views can be
  resized from the window corner, and each keeps its own size; the compact bars stretch to the window's width.

Counted the way ACT does it (and so Cactbot's overlays):
- Damage is every hit (normal, blocked, parried) plus damage-over-time ticks; healing includes heal-over-time ticks and
  overhealing (ACT's "Healed"). Pets, summons and ground effects count for their owner.
- An encounter starts at the first damage between you or your party and an enemy and ends when you leave combat (or
  change zone). DPS = damage / encounter time (first to last hit), the same time for everyone ("ENCDPS").
- DoT ticks name their source when the game sends it; when it doesn't, the tick goes to whoever has that status on the
  target, split evenly if several do. [Estimate] for that case.
- **rDPS / aDPS** (FFLogs-style raid-buff credit). On each hit, the extra damage another player's raid buff added is
  taken off the hitter (**aDPS** = damage without others' buffs) and, for **rDPS**, credited to whoever gave the buff.
  So an AST's rDPS includes what Divination and their cards added to everyone else. Your own buffs on yourself stay yours.
  - Damage-up buffs (Divination, The Balance / The Spear, Arcane Circle, Brotherhood, Embolden, Searing Light,
    Starry Muse, Technical / Standard Finish, Radiant Finale, Dokumori) are exact, split fairly when several stack.
    [Estimate]: Radiant Finale is taken as 3 codas (6%) and Technical/Standard Finish as full steps (5%).
  - Crit and direct hit rate buffs (Battle Litany, Chain Stratagem, Battle Voice, Devilment) need the hitter's own
    rates, estimated from their hits outside those buffs during the fight. [Estimate]. Guaranteed crit / direct hit
    skills (e.g. Inner Release) are credited like normal hits, so these buffs are slightly over-credited on them.
  - DoT ticks aren't re-credited (they lock in buffs when applied). [Estimate]: a small under-credit for buffers.
  - Only buffs from players the meter counts are moved (your party, or everyone with "count everyone").
  - Compact view shows rDPS in brackets when the rDPS column is on.

## Privacy
Party members are written as `p1`..`p7` with their job, never by name (the meter shows names on your screen only; it can hide them). Enemy names are kept.
Nothing is uploaded; files stay on your PC until you share them.

Requires nothing else. Works for any job.
