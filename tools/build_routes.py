#!/usr/bin/env python3
"""
Build dungeon route maps from Combat Recorder logs, for Mit Planner's Trash-mode lookahead
("is the next pack big, how soon, is the boss next?").

    python3 tools/build_routes.py LOG.jsonl.gz [...] --out DIR

Logs can be from any number of dungeons (4-player duties only; others are skipped).
Per run: combat segments (combat on -> off; segments < 4 s apart are merged), each with where it
started (your position), how long it lasted, how many enemies were in it, and the gap before it.
A segment is a boss when its biggest enemy casts abilities in the zone's timeline (cactbot or
learned, from Mit Planner) and has >= 2x the max HP of the dungeon's typical biggest enemy per
segment (median), or, whatever its name, >= 4x the max HP of the dungeon's typical biggest
enemy per segment (median). Absolute HP doesn't work: at level 100, trash has 5x the tank's HP;
relative to the dungeon's own trash, bosses stand out (Clyteum's Eye of the Scorpion ~10x+).

Across runs of the same dungeon, segments that start within MERGE_YALMS of each other are the
same stop on the route. Writes:
  <out>/<territory>.json   zone, runs, ordered stops: position, radius, mobs, pull length,
                           gap before, boss name (if any), how many runs saw it
  <out>/pacing.json        all runs together: median gap between pulls and pull length, used when
                           a dungeon has no map (or you're somewhere it doesn't know)
[Estimate] Stops seen in only one run are kept but marked; positions are where YOU stood when the
pull started, not where the mobs were.
"""
import argparse, gzip, json, math, os, re, statistics, sys, zipfile
from collections import defaultdict

MERGE_SECONDS = 4.0
MERGE_YALMS = 25.0
BOSS_FACTOR = 4.0


def load(path):
    with gzip.open(path, "rt", encoding="utf-8") as f:
        return [json.loads(l) for l in f if l.strip()]


def segments(path):
    ev = load(path)
    head = ev[0]
    if head.get("e") != "head" or len(head.get("party", [])) != 4 or not head.get("cfc"):
        return head, []
    ent = {}
    party_max = 1
    segs, cur = [], None
    last_t = 0.0
    for e in ev:
        k = e["e"]
        last_t = e.get("t", last_t)
        if k == "ent":
            ent[e["who"]] = (e.get("name", "?"), e.get("mhp", 0))
        elif k == "combat":
            if e["on"]:
                if cur is not None and cur["end"] is not None and e["t"] - cur["end"] < MERGE_SECONDS:
                    cur["end"] = None  # short break: same pull
                elif cur is None or cur["end"] is not None:
                    cur = dict(start=e["t"], end=None, snaps=[], enemies=set(), boss=None, bossHp=0)
                    segs.append(cur)
            elif cur is not None and cur["end"] is None:
                cur["end"] = e["t"]
        elif k == "snap" and cur is not None and cur["end"] is None:
            party_max = max([party_max, e["mhp"]] + [p["mhp"] for p in e.get("party", [])])
            cur["snaps"].append((e["t"], e["x"], e["z"]))
            ids = [x["who"] for x in e.get("enemyHp", [])]
            cur["enemies"].update(ids)
            for w in ids:
                name, mhp = ent.get(w, ("?", 0))
                if mhp > cur["bossHp"]:
                    cur["boss"], cur["bossHp"] = name, mhp  # biggest enemy so far; boss or not is decided per dungeon
    if cur is not None and cur["end"] is None:
        cur["end"] = last_t
    out = [s for s in segs if s["snaps"]]
    # a new segment starts only after the previous ended and the gap was long enough
    result = []
    prev_end = None
    for s in out:
        first = [p for p in s["snaps"] if p[0] - s["start"] <= 5] or s["snaps"][:1]
        x = statistics.mean(p[1] for p in first)
        z = statistics.mean(p[2] for p in first)
        result.append(dict(start=s["start"], end=s["end"], x=x, z=z, duration=s["end"] - s["start"],
                           mobs=len(s["enemies"]), boss=s["boss"], bossHp=s["bossHp"],
                           gap=None if prev_end is None else s["start"] - prev_end))
        prev_end = s["end"]
    return head, result


def timeline_sources(cactbot_zip, learned_dir):
    """territory -> names that cast abilities in its timeline (cactbot or learned): the bosses."""
    names = defaultdict(set)
    src = re.compile(r'source: "([^"]+)"')
    if cactbot_zip and os.path.exists(cactbot_zip):
        with zipfile.ZipFile(cactbot_zip) as z:
            for entry in json.loads(z.read("index.json")):
                path = f"timelines/{entry['path']}.txt"
                if path in z.namelist():
                    names[entry["territory"]].update(src.findall(z.read(path).decode("utf-8", "replace")))
    if learned_dir and os.path.isdir(learned_dir):
        for f in os.listdir(learned_dir):
            if f.endswith(".txt") and f[:-4].isdigit():
                with open(os.path.join(learned_dir, f), encoding="utf-8") as fh:
                    names[int(f[:-4])].update(src.findall(fh.read()))
    return names


def build(paths, out, sources=None):
    sources = sources or {}
    by_zone = defaultdict(list)
    for p in paths:
        try:
            head, segs = segments(p)
        except Exception as ex:  # a damaged file shouldn't stop the rest
            print(f"skip {os.path.basename(p)}: {ex}", file=sys.stderr)
            continue
        if segs:
            by_zone[(head["territory"], head["zone"])].append((os.path.basename(p), segs))
    os.makedirs(out, exist_ok=True)
    gaps, lengths = [], []
    for (territory, zone), runs in sorted(by_zone.items()):
        tops = [s["bossHp"] for _, segs in runs for s in segs if s["bossHp"]]
        typical = statistics.median(tops) if tops else 0
        known = sources.get(territory, set())
        for _, segs in runs:
            for s in segs:
                # Boss: named in the zone's timeline and clearly bigger than the trash (2x the typical
                # biggest enemy), or 4x by HP alone (timeline names a helper, or no timeline).
                big = typical and s["bossHp"] >= BOSS_FACTOR * typical
                named = s["boss"] in known and typical and s["bossHp"] >= 2 * typical
                if not (big or named):
                    s["boss"] = None
        stops = []  # each: dict(x, z, members=[(run, index, seg)])
        for r, (name, segs) in enumerate(runs):
            for i, s in enumerate(segs):
                if s["gap"] is not None and s["gap"] < 600: gaps.append(s["gap"])
                lengths.append(s["duration"])
                best = None
                for st in stops:
                    d = math.hypot(st["x"] - s["x"], st["z"] - s["z"])
                    if d <= MERGE_YALMS and all(m[0] != r for m in st["members"]) and (best is None or d < best[0]):
                        best = (d, st)
                if best:
                    st = best[1]
                    st["members"].append((r, i, s))
                    st["x"] = statistics.mean(m[2]["x"] for m in st["members"])
                    st["z"] = statistics.mean(m[2]["z"] for m in st["members"])
                else:
                    stops.append(dict(x=s["x"], z=s["z"], members=[(r, i, s)]))
        # order: by each stop's median position in its runs (as a share of that run's length)
        def order(st):
            return statistics.median(m[1] / max(1, len(runs[m[0]][1]) - 1) for m in st["members"])
        stops.sort(key=order)
        # Run count only: file names show when and on which job you played (this ships in a public repo).
        doc = dict(territory=territory, zone=zone, runs=len(runs), stops=[])
        for st in stops:
            segs = [m[2] for m in st["members"]]
            boss = next((s["boss"] for s in segs if s["boss"]), None)
            g = [s["gap"] for s in segs if s["gap"] is not None]
            radius = max([math.hypot(s["x"] - st["x"], s["z"] - st["z"]) for s in segs] + [0.0])
            doc["stops"].append(dict(
                x=round(st["x"], 1), z=round(st["z"], 1), radius=round(max(10.0, radius + 5), 1),
                mobs=round(statistics.median(s["mobs"] for s in segs), 1),
                duration=round(statistics.median(s["duration"] for s in segs), 1),
                gapBefore=round(statistics.median(g), 1) if g else None,
                boss=boss, seen=len(segs)))
        with open(os.path.join(out, f"{territory}.json"), "w") as f:
            json.dump(doc, f, indent=1)
        bosses = sum(1 for s in doc["stops"] if s["boss"])
        print(f"{zone} ({territory}): {len(runs)} run(s), {len(doc['stops'])} stops ({bosses} boss), "
              f"seen in 2+ runs: {sum(1 for s in doc['stops'] if s['seen'] >= 2)}")
    pacing = dict(gapMedian=round(statistics.median(gaps), 1) if gaps else 30.0,
                  gapP25=round(statistics.quantiles(gaps, n=4)[0], 1) if len(gaps) > 3 else 20.0,
                  gapP75=round(statistics.quantiles(gaps, n=4)[2], 1) if len(gaps) > 3 else 60.0,
                  pullMedian=round(statistics.median(lengths), 1) if lengths else 45.0,
                  samples=len(gaps))
    with open(os.path.join(out, "pacing.json"), "w") as f:
        json.dump(pacing, f, indent=1)
    print("pacing:", pacing)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("logs", nargs="+")
    ap.add_argument("--out", required=True)
    here = os.path.dirname(os.path.abspath(__file__))
    ap.add_argument("--cactbot", default=os.path.join(here, "..", "MitPlanner", "Timelines", "cactbot.zip"))
    ap.add_argument("--learned", default=os.path.join(here, "..", "MitPlanner", "Timelines", "Learned"))
    a = ap.parse_args()
    build(a.logs, a.out, timeline_sources(a.cactbot, a.learned))


if __name__ == "__main__":
    main()
