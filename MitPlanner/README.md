# Mit Planner

Boss timelines with countdowns, plus a per-job plan of which cooldowns to use on
which mechanics.

Commands: `/mitplan` (settings), `/mitplan on` / `off` (run the plan), `/mitplan auto on` / `auto off`,
`/mitplan preview`, `/mitplan stop`, `/mitplan timeline`.

## How it works
- **Fight detection:** entering any duty cactbot has a timeline for loads it.
  All ~310 cactbot timelines (dungeons, trials, raids, alliance raids, ultimates,
  Eureka/Bozja/Occult) ship inside the plugin as one compressed bundle (~750 KB).
  Only a small index is read at startup; a fight's timeline is unpacked the first
  time you enter it or open it in the Plan tab. Fight names come from the game.
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
  Off by default. If the cooldown hasn't started within 0.25 s it presses again,
  every 0.25 s, until it goes off or the mechanic lands (the game queues
  off-GCD presses during animation lock, so repeats are harmless).
- **On/off:** "Plan", "Auto" and "Callouts" buttons on the timeline window.
  Plan off = timeline only, no callouts, no auto-use. Callouts off hides the
  callout window only (auto-use still runs if Auto is on).
- **New cooldowns default to Auto ticked**; nothing auto-presses until the
  Auto: ON switch is on (off by default).
- **Columns:** in both the Plan table and the timeline window, drag a column
  edge to resize and a header to reorder (timeline: right-click a header to
  hide a column). ImGui remembers the layout.
- **Picking a fight:** the Plan tab selects the duty you're in whenever you enter one with a
  timeline. The fight dropdown has a search box (name,
  category such as "DT Raid" or "EW Trial", or zone ID). Newest expansion first.
- **Adding cooldowns:** the "+ Add cooldown" list has a search box and can be
  sorted by cooldown length or A-Z (remembered).

## MT / OT
Plans are kept separately for MT and OT (per fight, per job). The **Role: MT/OT**
button on the timeline window (or `/mitplan mt|ot`) picks which one runs.

## Starter plans (tanks)
Plan tab > **Generate starter plan** builds one from these rules:
- **MT**, every tank buster: Short mit, plus Big if it'll be back in time;
  otherwise Rampart + Secondary.
- **OT**, every tank buster: send the OT Sent Mit to whoever it targets (normally
  the MT), plus Short on yourself only if the buster targets you.
- **OT and tank busters:** the OT sends its Sent mit to the MT when the buster isn't on
  it, and uses the full set (Short + Big, or Rampart + Secondary) when it is: many busters hit
  both tanks or swap. **Shared tank busters** (cactbot's "shared tank buster", badge "TB x2")
  get the full set from both tanks, always.
- **Raid-wides: one Reprisal + one party mit on every hit, from different
  tanks.** #1: MT Reprisal + OT party mit. #2: MT party mit + OT Reprisal. Then
  repeat. Both tanks' cooldowns are simulated together, so the MT's and the OT's
  generated plans fit each other. If two raid-wides are too close for that
  (Reprisal 60 s, party mits 90 s), each gets whatever is back and the notes
  list the gaps.
- **Press timing** (seconds before the hit; 1.25 s between presses so they
  don't collide on animation lock): Big 3.25 then Short 2.0; if Big isn't back,
  Rampart takes Big's slot with Secondary 0.75 s ahead of it: Secondary 4.0,
  Rampart 3.25, Short 2.0. Raid-wides: Reprisal 5.0, party mit 3.75. OT Sent Mits 2.0.
- Invulns are never planned.

| | Big | Secondary | Short | OT Sent Mit | Party mit |
|---|---|---|---|---|---|
| PLD | Sentinel/Guardian | Bulwark | (Holy) Sheltron | Intervention | Divine Veil |
| WAR | Vengeance/Damnation | Thrill of Battle | Raw Intuition/Bloodwhetting | Nascent Flash | Shake It Off |
| DRK | Shadow Wall/Shadowed Vigil | Oblation | The Blackest Night | TBN on the MT | Dark Missionary |
| GNB | Nebula/Great Nebula | Camouflage | Heart of Stone/Corundum | HoC on the MT | Heart of Light |

All use Rampart when Big isn't back, and split Reprisal / party mit on raid-wides as above. Sheltron (Oath gauge) and TBN (MP) cost
resources the cooldown check can't see.

## OT Sent Mits
Abilities the game lets you cast on a party member (Intervention, Nascent Flash,
TBN, Heart of Corundum, ...) get a **Send** box; self-only ones (Guardian,
Rampart, ...) don't. Tick **Send** to cast it on whoever the mechanic targets instead
of your own target (falls back to the other tank). Pair it with "If I'm NOT
targeted". Callouts read "Send Nascent Flash to <name>".

## "Only if targeted"
Each planned cooldown has a condition: **Always**, **If I'm targeted**, or
**If I'm NOT targeted**. Cooldowns added to a tank buster default to
"If I'm targeted". Who a mechanic targets is checked live, best evidence first:
1. **Head marker** cactbot associates with the mechanic (Castrum Marinum:
   Divide Et Impera's tank-buster marker 00DA).
2. **The boss's cast target** (e.g. Emerald Shot, Stamp).
3. **Aggro**: who the boss is attacking, if neither has happened yet.

Callouts show the verdict and source, e.g. `[on you: cast]`; if it's not for you
the line turns grey ("skip"). Auto-use never fires when the condition fails or the
target can't be determined. Because it's checked live, it follows tank swaps.

## Fight loops
Some fights loop (Castrum Marinum repeats parts of both phases). When the
timeline jumps back, planned cooldowns for the repeated mechanics re-arm.

## Plan QC
The Plan tab checks every planned cooldown against its recharge time (including
abilities with charges, which recharge one at a time). If a use comes before the
cooldown is back, e.g. Rampart twice inside 90 s, it's flagged in red with the time
it's actually ready, and the timeline window shows a red "!" next to its icon.

## Times
The Plan tab shows **fight time** as `m:ss (Ns)`. cactbot puts later phases on
placeholder clocks (phase 2 starts at 1000 s), so long empty gaps are collapsed;
times after a phase change are marked `~` because they depend on kill speed.
QC uses the same fight time.

## Saving and moving plans
Plans and tags save automatically, per fight and per job, in Dalamud's plugin
settings on that PC. To move a plan to another PC (or share it): Plan tab >
**Copy plan to clipboard**, then **Paste plan** on the other PC (same fight, same job).

## Testing without a duty
Display > **Preview** runs the timeline from 0 anywhere (no syncing, no auto-use),
so you can check plans and callout placement before queueing.

## Where the timelines and tags come from
Three layers, later ones win:
1. **cactbot triggers** (automatic, every fight): a mechanic is tagged when
   cactbot's trigger for that ability calls it out as a tank buster, raid-wide,
   stack, or a movement mechanic (spread, knockback, get out/in...). About 280
   of the 310 fights get at least one tag this way.
   Also matched **by ability name** (via game data): cactbot often triggers on the cast
   while the timeline syncs on the damage, which are different action IDs with the same name.
   **Head-marker mechanics** (the tank-buster icon over a tank, used by most newer fights)
   can't be tied to a timeline line ahead of time. The plugin learns them in game: when an
   enemy ability hits someone who had cactbot's tank-buster (or stack) marker, that line is
   tagged and a chat line says so ("Learned: X = tank buster"). Learned tags sit below your
   own edits; change any of them in the Plan tab. They also make "if targeted" conditions work.
   When a tank buster is learned, mits for each hit are **added to your plan** (current role),
   slotted around what's already planned by the cooldown check; chat lists what was added.
   **Safety net:** when cactbot's tank-buster marker lands on you (or its shared-buster marker
   on a tank) and nothing planned or just used covers it, the plugin calls out the best mits
   off cooldown right now (Big, else Rampart + Secondary; plus Short) and presses them if
   auto-use is allowed. Both are on by default (Display tab).
2. **Curated list** (`MitPlanner/CuratedFights.cs`): hand-checked tags and head
   markers. Currently The Cinder Drift, Castrum Marinum and The Cloud Deck.
3. **Your edits** in the Plan tab's type column.

**Updates:** every Tuesday a GitHub job re-bundles the timelines from cactbot.
If anything changed, Mit Planner's version goes up by one in the last digit
(0.8.0.0 to 0.8.0.1) and Dalamud offers the update. Your plans and tag edits
are untouched: they're keyed by mechanic name and hit number (Stamp #2), so a
plan only loses an entry if cactbot renames or removes that mechanic.

## Known limits
- The clock only syncs on boss abilities and cast bars (cactbot's `Ability` and
  `StartsUsing` lines, about 97% of sync lines). Lines that sync on other things
  (system messages, NPC yells, actors appearing) are shown but don't re-sync;
  the next ability sync catches the clock up.
- Tags from cactbot are only as good as its triggers: a buster cactbot doesn't
  call out (or calls out with custom text) stays untagged until you tag it or
  it's added to the curated list.
- Accuracy depends on the cactbot timeline. If the clock drifts, the Status tab
  shows the last sync.

Timelines and default tags from cactbot (Apache-2.0); see
[THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md).
