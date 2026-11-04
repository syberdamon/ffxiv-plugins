# Combat logs: plan

Goal (syberdamon, 2026-11-02): build up a library of real fights now. It will be used to tune
Mit Planner's Trash mode, find lost DPS (downtime, drift, clipping), and later build our own
rotation and healing helpers, so we aren't dependent on third-party rotation plugins that may go
paid.

## Sources
- **Combat Recorder** (this repo): full fight data per duty, in
  `pluginConfigs/CombatRecorder/logs/*.jsonl.gz`. The format and event types are described in
  the header comment of `CombatRecorder/Recorder.cs`.
- **Mit Planner Trash-mode logs**: what Trash mode decided and why, in
  `pluginConfigs/MitPlanner/trashlog/*.log`.

## Storage
- **Private repo `syberdamon/ffxiv-combat-logs`** (created 2026-11-03). Logs never go in this
  public repo, because they show party jobs, timestamps and where you play.
- Layout:
  - `raw/<yyyy-mm>/<file>.jsonl.gz`
  - `trash/<yyyy-mm>/<file>.log`
  - `notes/<date>.md` (review findings, and the marks you pointed to)
- Claude copies files from the linked pluginConfigs folder into that repo when asked to review.

## Review workflow
1. Play with Combat Recorder (and Trash mode) on. Use `/crec mark <note>` to flag moments to
   look at.
2. Ask "review the logs". Claude pulls the new files, runs the analysis scripts (`tools/`),
   writes findings to `notes/`, and proposes changes (thresholds, rules, rotation fixes).

## Analyses planned
- **Mitigation:** Trash-mode flags from `tools/trashlog_report.py` (unneeded, pull-end,
  overlap, late).
- **DPS:**
  - GCD uptime and gaps (time with no GCD while a target was in range and targetable)
  - buff window alignment
  - drift on 60/120 s cooldowns
  - downtime around marks
  - positionals (using facing)
- **Healing:**
  - party HP curves vs incoming damage
  - overheal (heal amount vs missing HP)
  - time spent below thresholds, by healer job

## Outside baselines (see `references/SOURCES.md` in the log repo)
- **The Balance:** rotation and opener baselines per job and patch.
- **FFLogs:** real extreme/savage clears and top parses to compare against (v2 API needs a free
  client ID + secret, never committed).
- **xivanalysis:** per-job checks (GCD uptime, buff windows, drift, overcap). Open source,
  MIT license, so its rules can be adapted with credit.

## Learned timelines (fights cactbot doesn't cover)
Decided 2026-11-04: built offline from Combat Recorder logs, reviewed by Claude, then shipped
with Mit Planner. Any fight without a cactbot timeline. Used once every boss has 2+ recorded kills.
1. Record a few runs of the duty with Combat Recorder on.
2. Ask for a timeline. Claude copies the logs to the private log repo and runs
   `python3 tools/build_timeline.py <logs...> --out <dir>`. It writes the timeline, an index entry
   (tags, kills, `trusted`) and a review report (per ability: timing spread, who it hit, damage).
3. After review, the `.txt`/`.json` go into `MitPlanner/Timelines/Learned/` and ship with the next
   Mit Planner release. Mit Planner loads trusted ones for zones cactbot has no timeline for.
How it reads a fight:
- **Boss:** an enemy with 8x your max HP.
- **Timing:** time 0 is the first hit on the boss; the n-th use of each ability is lined up across
  pulls (median time); the timeline syncs on the opener's cast bar.
- **Lines kept:** casts and their follow-up hits; un-cast hits only if they landed on 2+ players.
- **Classification:**
  - hit 75%+ of the party for 5%+ HP → raidwide (a stack if the cast targeted a player);
  - a cast that hit only a tank → tankbuster; only both tanks → shared tankbuster;
  - any other cast → mechanic.
- **Loops:** the repeating tail of the fight becomes a cactbot-style loop.
Checked against cactbot's Mistwake timeline (1 run): opener times match within 0.5 s, and the tank
busters, raidwides and stack match cactbot's tags.
