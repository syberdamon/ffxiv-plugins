# Dungeon Mit (working name): rules draft

Status: **built as Mit Planner 0.9.0.0 "Trash mode"** (decisions below, 2026-11-02). Written 2026-11-02 from syberdamon's
brief plus community tanking guides (sources at the end). Items marked **[Assumption]** or
**[Verify]** need a check in game or a decision from you.

## Goal

Run tank mitigation on its own through most dungeon runs, mainly trash pulls. Mit Planner
(cactbot timelines) already handles bosses. Dungeon Mit reacts to what's happening right now:
how many mobs are on you, your HP, whether the pull has settled, and how long the pull will
probably last.

## Principles (sourced)

1. **Trash is where tanks die, not bosses.** "Use mitigation on trash, not bosses. Dungeon
   bosses do very little damage and trash pulls can hurt." (Icy Veins)
2. **Mitigate proactively, at high HP.** Using defensives after you've already taken the damage
   is less effective. (FFXIV Wiki, Tank Guide)
3. **Stagger, don't stack, the big ones.** "Try not to overlap bigger cooldowns like Rampart,
   your 40%, and Arm's Length, so that you will always have something available." Stacked
   mitigation multiplies, so overlapping gives diminishing returns. (Icy Veins; FFXIV Wiki)
4. **Keep something up at all times.** (Icy Veins)
5. **Reprisal every pack.** "Reprisal has a short cooldown, use it every pack." (Icy Veins)
6. **Arm's Length is mitigation.** Its 20% slow on attackers means about 20% less incoming
   auto-attack damage. (Icy Veins)
7. **Invulns are proactive tools.** Use them where they save the most healing, not only as a
   last-second save. (Icy Veins; FFXIV Wiki)
8. **Hold mits while mobs are stunned** (e.g. a White Mage's Holy stun). Use them once the
   stun wears off. (Icy Veins)
9. **Dead mobs don't hit you.** Near the end of a pull, mitigation is worth less. (FFXIV Wiki)

## What the plugin watches (signals)

| Signal | How | Notes |
|---|---|---|
| **Mobs on you** | Enemy (hate) list, counting mobs whose target is you | Counted only within about 8y, so far-off stragglers don't count |
| **Mobs gathered** | Share of those mobs within 5y of you | "Pull is set" when at least 70% are in |
| **Stationary** | Your position has moved less than 0.3y for **1.5 s** (configurable) | Main gate against spending mits mid-pull. You run pack to pack, so moving = still pulling |
| **HP %** and **damage rate** | HP now, and HP lost per second over the last 2 s | Damage rate lets it react to spikes before HP is low |
| **Pull time left** | Sum of engaged mobs' current HP ÷ party damage rate (mob HP lost per second, last 4 s) | No reading for the first ~4 s; treat as "long" if 3+ mobs average above 50% HP |
| **Active mits** | Your own status effects (Rampart, 40%, Bloodwhetting...) | Read from the game, not timers, so mits you press yourself or another plugin presses are counted |
| **Mobs stunned** | Stun status on most engaged mobs | Hold non-emergency mits until it ends |
| **Boss fight** | A Mit Planner timeline is running, or a single high-HP enemy | Dungeon Mit stands down except emergencies (see Bosses) |

## The pull state machine

1. **Idle:** fewer than the mob threshold on you, or out of combat.
2. **Pulling:** mobs are on you but you're moving or they aren't gathered. **No mits**, except
   the emergency rule. This is your "1–2 pulls' worth at once, don't blow mits in between".
3. **Set:** stationary for the hold time (1.5 s default), mobs ≥ threshold, mobs gathered. The
   rotation runs.
4. **Winding down:** pull time left < 6 s, or 1 mob left. No new big mits; shorts and heals only
   if HP is low.

Moving again doesn't cancel what's already active, but it pauses new presses (back to Pulling).

## Thresholds (defaults, all configurable)

| Setting | Default | Your brief |
|---|---|---|
| Mob threshold for a "pull" | **3+** | "more than 3 mobs" |
| HP for reactive rules | **< 80%** | "less than 80% health" |
| Stationary hold | **1.5 s** | 1.5 s |
| Heavy (40%) only if pull time left ≥ | **12 s** | "10–15 seconds or more" |
| No new big mits when pull time left < | **6 s** | (added) |
| Emergency HP | **< 35%** (or < 50% while losing > 15%/s) | (added) |

**Pushback on the HP gate.** If rules wait until HP < 80%, every pull opens reactively, which
the guides advise against (principle 2). Proposal: the **opener fires on "Set" with 3+ mobs at
any HP**. The **HP < 80% rule escalates** within a pull (adds the next layer sooner). Your call;
it's one toggle ("Proactive opener").

## The rotation (per pull, in order)

Layers fire one at a time, with at least 1.25 s between presses (animation lock, same as Mit
Planner). Before each press, the plugin checks: in "Set", not winding down, mobs not stunned,
off cooldown, enough resource (gauge/MP), and not already active.

**L1 Opener** (on Set):
- **Short mit** (WAR Bloodwhetting / PLD Holy Sheltron / DRK The Blackest Night / GNB Heart of
  Corundum).
- **Reprisal** (every pack, principle 5) when 3+ mobs.
- **Rampart**: same time as the Short, **except WAR**. See the WAR rule below.

**L2 Heavy (40%)** (Damnation / Guardian / Shadowed Vigil / Great Nebula):
- Pull time left ≥ 12 s, and **no other big mit active** (staggering, principle 3), and either
  HP < 80%, or 5+ mobs, or the previous big mit just expired with the pull still long.
- On big pulls (5+ mobs) the 40% goes **first** and Rampart waits for it to expire. On normal
  pulls Rampart goes first and the 40% covers the next gap.

**L3 Gap fill** (keeps "something up at all times", principle 4): when a big mit expires and
the pull is still ≥ 8 s long, the next available one goes in this order:
1. Rampart or the 40% (whichever is back)
2. **Arm's Length** (principle 6)
3. **Secondary**: WAR Thrill of Battle / PLD Bulwark / DRK Oblation / GNB Camouflage
4. **Short again** whenever it's back (Bloodwhetting and Heart of Corundum are 25 s; TBN every
   time it breaks and MP allows; Sheltron on gauge)

**L4 Heals** (when HP < 70% and nothing heal-over-time is running):
- WAR Equilibrium; GNB Aurora; DRK Abyssal Drain.
- Ask decision: **party mits as heals?** (Shake It Off / Divine Veil / Dark Missionary / Heart of
  Light). In trash only the tank is usually hit, so the default is to keep them for bosses.
- WAR caution: **Shake It Off consumes Thrill of Battle, Damnation and Bloodwhetting**, so it
  never fires while any of those are on you. [Verify current wording in game]

**L5 Emergency** (any state, even while moving):
- HP < 35%, or < 50% while losing > 15%/s:
  - Short (if back), then Rampart or the 40% (if back), then the heal.
  - If HP is still falling and < 20%: **invuln**.
- Invuln defaults per job:
  - **PLD Hallowed Ground: on**
  - **WAR Holmgang: on**
  - **DRK Living Dead: off** (needs heals to come back out; opt in)
  - **GNB Superbolide: off** (drops you to 1 HP; opt in)
- Optional "**big pull invuln**": 6+ mobs, Set, and no 40% available → invuln as the opener
  (principle 7). Off by default.

### WAR rule (yours)

Rampart waits until **Bloodwhetting has worn off**, so the two cover back to back instead of
overlapping while Bloodwhetting's healing already keeps you topped up.

This lines up with the staggering principle. One guard: if HP < 70% or the emergency rule fires
while Bloodwhetting is still up, Rampart goes in immediately. [Assumption] Bloodwhetting's
damage reduction lasts 8 s (Stem the Flow's extra part 4 s); the plugin reads the status, so
exact durations don't matter.

## Bosses

- If Mit Planner has a timeline running for the boss, Dungeon Mit only runs **L5 Emergency**.
  Mit Planner's plan and its tank-buster safety net handle the rest.
- If there's no timeline (a dungeon boss cactbot doesn't cover), it uses the same rules with the
  mob count treated as satisfied. Dungeon bosses hit lightly, so only L1 (on the pull) and the
  HP-based rules run.

## Level sync

Everything uses the abilities you have at the dungeon's synced level, using the per-level ability
list built for Mit Planner 0.8.4. At level 49 a WAR has Vengeance, Thrill, Rampart, Reprisal,
Arm's Length and Holmgang, but no Bloodwhetting/Raw Intuition (level 56). The rules skip missing
layers automatically.

## Safety & conflicts

- **Master switch** plus **Auto-press** (same as Mit Planner's "Allow auto-use"). With auto-press
  off it only shows callouts.
- **Conflicts:** Wrath Combo, RotationSolver and BossMod's Tank AI ("Personal mits") can all
  press defensives. Two systems doing it waste cooldowns. Dungeon Mit sees active mits and won't
  double up, but **turn off their defensive options** to keep it predictable. [Verify which of
  yours are on]
- Never presses while casting, mounted, dead, or out of combat.
- Log line per press ("Pull 4: 5 mobs, 62% HP → Damnation"), so it's easy to tune afterwards.

## Decisions (syberdamon, 2026-11-02)

1. **Shape:** Trash mode inside Mit Planner.
2. **Trigger:** pull set **or** HP below a threshold (default 60%, 2+ mobs) even while gathering
   or moving out of AoEs.
3. **Heals on trash:** yes, party mits included (healers carry bosses).
4. **Invuln:** when N seconds (default 2, configurable) of incoming damage with no heals would
   kill you and no major mit is up or ready. Per-job switches (PLD/WAR on, DRK/GNB off).
5. **Run log** for tuning: `pluginConfigs/MitPlanner/trashlog/`, one file per dungeon.

## Warrior: Bloodwhetting-only (syberdamon, 2026-11-02)

Once WAR's short cooldown heals on hit (Raw Intuition, later Bloodwhetting; default level 56,
configurable), that heal caps HP on multi-mob AoE pulls. So in trash, Trash mode uses **only that
short cooldown** (every time it's back). Everything else (Reprisal, Rampart, the 40%, Thrill,
heals) waits until it has **worn off and HP is below 70%** (configurable). Emergency and invuln
rules still apply.

## Direction: adaptive mitigation

The goal is a plan that adapts on the fly to the healer, the tank job, and possibly the dungeon,
with thresholds that still hold up with weak healers. How we get there:

1. **Logs first.** Each run records the party (healer job), the dungeon, every press and its
   reason, what was held back, and once-a-second HP, damage taken and pull-length estimates.
2. **Review on request.** `tools/trashlog_report.py` flags mits pressed while HP stayed full
   (UNNEEDED), mits wasted at the end of a pull (PULL-END), big mits stacked on each other
   (OVERLAP), low HP with nothing pressed (LATE), emergencies, invulns and deaths.
3. **Then profiles.** Once there's data, add per-healer, per-job and per-dungeon threshold
   profiles (e.g. a lighter touch with a strong healer, an earlier escalate HP with a weak one).
   Possibly adjusted live from how fast HP comes back after hits.

## Sources

- Icy Veins, Introduction to Tanking in FFXIV: https://www.icy-veins.com/ffxiv/introduction-to-tanking
- FFXIV Wiki (consolegameswiki), Tank Guide: https://ffxiv.consolegameswiki.com/wiki/Tank_Guide
- The Balance, Warrior Basic Guide: https://www.thebalanceffxiv.com/jobs/tanks/warrior/basic-guide/

## Changes from log review 2026-11-04 (Mit Planner 0.9.5)
- **Short mit while gathering:** with 3+ mobs on you and HP below 80%, the short mit (Bloodwhetting,
  Sheltron, ...) goes out while you're still moving. Lunar Subterrane WAR 90: HP fell from ~90% to
  57-65% while gathering 8-9 mobs, and one pull bottomed at 40% right after.
- **No big mit at full HP with little incoming:** HP >= 95% and < 3%/s incoming holds Rampart and
  friends. Rampart went out twice at 100% HP, ~0%/s, with the pull nearly dead.
