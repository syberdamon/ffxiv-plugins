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
