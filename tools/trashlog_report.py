"""Summarise Mit Planner Trash-mode logs and flag inefficiencies.

Usage: python trashlog_report.py <log file or folder> [more ...]

Reads the tab-separated logs written by MitPlanner/TrashLog.cs
(time, EVENT, details with key=value fields) and prints a Markdown report per file:
  - run header: dungeon, job/level, party (healer type), plugin version
  - per pull: duration, lowest HP, presses, deaths
  - flags:
      UNNEEDED  a mit pressed while HP stayed >= 90% and damage stayed low for its duration
      PULL-END  a big mit pressed less than 5 s before the pull ended
      OVERLAP   a big mit pressed while another big mit was already up
      LATE      HP fell below 40% with nothing pressed (by Trash mode or you) in the 5 s before
      DEATH / INVULN / EMERGENCY occurrences
  - totals and which signals to tune.
"""
import re
import sys
from datetime import datetime, timedelta
from pathlib import Path

BIG = {"Rampart", "Vengeance", "Damnation", "Sentinel", "Guardian", "Shadow Wall", "Shadowed Vigil",
       "Nebula", "Great Nebula", "Arm's Length"}


def parse(path):
    rows = []
    for line in Path(path).read_text(encoding="utf-8", errors="replace").splitlines():
        parts = line.split("\t")
        if len(parts) < 2:
            continue
        try:
            t = datetime.strptime(parts[0], "%H:%M:%S.%f")
        except ValueError:
            continue
        fields, free = {}, []
        for p in parts[2:]:
            if "=" in p:
                k, v = p.split("=", 1)
                fields[k] = v
            else:
                free.append(p)
        rows.append({"t": t, "ev": parts[1], "f": fields, "text": " ".join(free), "raw": line})
    # handle midnight rollover
    day = timedelta(0)
    for i in range(1, len(rows)):
        if rows[i]["t"] + day < rows[i - 1]["t"] - timedelta(hours=1):
            day += timedelta(days=1)
        rows[i]["t"] += day
    return rows


def num(v, default=None):
    if v is None:
        return default
    m = re.match(r"-?[\d.]+", v)
    return float(m.group()) if m else default


def report(path):
    rows = parse(path)
    out = [f"## {Path(path).name}"]
    zone = next((r for r in rows if r["ev"] == "ZONE"), None)
    if zone:
        z = zone["f"]
        out.append(f"- Dungeon **{z.get('zone', '?')}**, {z.get('job', '?')}, party `{z.get('party', '?')}`, plugin {z.get('plugin', '?')}")
    snaps = [r for r in rows if r["ev"] == "SNAP"]
    presses = [r for r in rows if r["ev"] in ("PRESS", "CALLOUT")]
    used = [r for r in rows if r["ev"] in ("PRESS", "USED")]
    flags = []

    for p in presses:
        name = p["text"].split(" ")[0] if p["text"] else "?"
        name = p["raw"].split("\t")[2] if len(p["raw"].split("\t")) > 2 else name
        after = [s for s in snaps if p["t"] <= s["t"] <= p["t"] + timedelta(seconds=10)]
        if after and all(num(s["f"].get("hp"), 0) >= 90 and num(s["f"].get("dmg"), 0) < 3 for s in after):
            flags.append(("UNNEEDED", p["t"], f"{name} ({p['f'].get('reason', '')}): HP stayed >= 90%, damage < 3%/s"))
        if name in BIG:
            # "up" is captured just after the press, so it can include the action itself.
            up = [u for u in p["f"].get("up", "[]").strip("[]").split(",") if u and u != name]
            if any(u in BIG for u in up):
                flags.append(("OVERLAP", p["t"], f"{name} while {p['f'].get('up')} was up"))
            end = next((r for r in rows if r["ev"] == "END" and r["t"] >= p["t"]), None)
            if end and (end["t"] - p["t"]).total_seconds() < 5:
                flags.append(("PULL-END", p["t"], f"{name} {(end['t'] - p['t']).total_seconds():.1f}s before the pull ended"))

    low_flagged = None
    for s in snaps:
        hp = num(s["f"].get("hp"), 100)
        if hp < 40 and (low_flagged is None or (s["t"] - low_flagged).total_seconds() > 10):
            recent = [u for u in used if s["t"] - timedelta(seconds=5) <= u["t"] <= s["t"]]
            if not recent:
                flags.append(("LATE", s["t"], f"HP {hp:.0f}% with nothing pressed in the last 5 s (mobs={s['f'].get('mobs')}, dmg={s['f'].get('dmg')}, up={s['f'].get('up')}, state={s['f'].get('state')})"))
                low_flagged = s["t"]
    for r in rows:
        if r["ev"] in ("DEATH", "INVULN", "EMERGENCY"):
            flags.append((r["ev"], r["t"], f"hp={r['f'].get('hp')} dmg={r['f'].get('dmg')} mobs={r['f'].get('mobs')} up={r['f'].get('up')}"))

    out.append("\n| Pull | Duration | Lowest HP | Presses | Died |\n|---|---|---|---|---|")
    for i, e in enumerate([r for r in rows if r["ev"] == "END"], 1):
        f = e["f"]
        out.append(f"| {i} | {f.get('duration')} | {f.get('minhp')} | {f.get('presses')} | {f.get('dead')} |")

    skips = {}
    for r in rows:
        if r["ev"] == "SKIP":
            key = r["raw"].split("\t")[2] if len(r["raw"].split("\t")) > 2 else "?"
            key = re.sub(r"\d+(\.\d+)?%", "N%", re.sub(r"\(HP .*?\)", "", key)).strip()
            skips[key] = skips.get(key, 0) + 1

    out.append(f"\n**Flags ({len(flags)})**")
    for kind, t, msg in sorted(flags, key=lambda x: x[1]):
        out.append(f"- `{t:%H:%M:%S}` **{kind}** {msg}")
    if skips:
        out.append("\n**Held back (count)**: " + "; ".join(f"{k} x{v}" for k, v in sorted(skips.items(), key=lambda x: -x[1])))
    out.append(f"\nTotals: {len(presses)} presses/callouts, {len([r for r in rows if r['ev'] == 'USED'])} by you/other, "
               f"{sum(1 for f in flags if f[0] == 'UNNEEDED')} unneeded, {sum(1 for f in flags if f[0] == 'LATE')} late, "
               f"{sum(1 for f in flags if f[0] == 'DEATH')} deaths")
    return "\n".join(out)


def main():
    files = []
    for arg in sys.argv[1:]:
        p = Path(arg)
        files += sorted(p.glob("*.log")) if p.is_dir() else [p]
    print("\n\n".join(report(f) for f in files) if files else "No logs found.")


if __name__ == "__main__":
    main()
