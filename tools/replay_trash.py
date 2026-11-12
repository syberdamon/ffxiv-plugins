#!/usr/bin/env python3
"""
Replay Trash-mode runs through the lookahead decision (MitPlanner/Decision/MitScorer.cs, mirrored
here): for every time the big mit went out, would the new logic have used it or kept it for the
next pack/boss, and what did the next pull look like?

    python3 tools/replay_trash.py --trash TRASH_DIR --raw RAW_DIR [--cactbot ZIP --learned DIR]

Positions come from the Combat Recorder file of the same run (matched by zone, job and start
time). Route maps are rebuilt leaving the replayed run out, so a run is never judged with
knowledge of itself. Runs without a matching recording use pacing only.
Keep the formulas identical to MitScorer.cs.
"""
import argparse, bisect, datetime as dt, gzip, importlib.util, json, math, os, re, statistics, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("build_routes", os.path.join(HERE, "build_routes.py"))
br = importlib.util.module_from_spec(spec); spec.loader.exec_module(br)

BIG = {"Vengeance", "Damnation", "Sentinel", "Guardian", "Shadow Wall", "Shadowed Vigil", "Nebula", "Great Nebula"}

# ---- MitScorer mirror
BOSS_RATE, HOLD_BIAS, CRIT_HP, CRIT_S = 4.0, 1.25, 45.0, 4.0
TYPICAL_MOBS, BOSS_SHARE_NO_MAP = 5.0, 0.3


def danger(hp): return 1 + 2 * min(1, max(0, (70 - hp) / 40))


def critical(hp, net): return hp < CRIT_HP or hp - max(0, net) * CRIT_S < 20


def evaluate(hp, inc, net, mobs, rest, red, dur, recast, nxt):
    now = inc * min(dur, max(0, rest)) * red * danger(hp)
    held = 0.0
    if recast > nxt["until"] and math.isfinite(nxt["until"]):
        per_mob = inc / mobs if mobs > 0 else inc
        pack = per_mob * max(1, nxt["mobs"])
        rate = nxt["boss_chance"] * BOSS_RATE + (1 - nxt["boss_chance"]) * pack
        window = min(dur, max(1, nxt["dur"]))
        missed = min(1, max(0, (recast - nxt["until"]) / max(1, nxt["dur"])))
        held = rate * window * red * missed
    if critical(hp, net): return now, held, True, "survival"
    if held <= 0: return now, held, True, "back before the next pull"
    use = held <= now * HOLD_BIAS
    return now, held, use, "worth more now" if use else ("keep for the boss" if nxt["boss"] else "keep for the next pack")


def lookahead(route, pace, pos, elapsed, ttk):
    if route and pos:
        best, bd = -1, 1e9
        for i, s in enumerate(route["stops"]):
            d = math.hypot(pos[0] - s["x"], pos[1] - s["z"])
            if d <= s["radius"] and d < bd: best, bd = i, d
        if best >= 0:
            here = route["stops"][best]
            rest = ttk if ttk is not None else max(5, here["duration"] - elapsed)
            if best + 1 < len(route["stops"]):
                n = route["stops"][best + 1]
                boss = n["boss"] is not None
                return dict(rest=rest, until=rest + (n["gapBefore"] if n["gapBefore"] is not None else pace["gapMedian"]),
                            mobs=0 if boss else n["mobs"], boss=boss, boss_chance=1.0 if boss else 0.0, dur=n["duration"],
                            src=f"map {best + 1}/{len(route['stops'])}", next_boss_name=n["boss"])
            return dict(rest=rest, until=math.inf, mobs=0, boss=False, boss_chance=0, dur=0, src="map: last stop", next_boss_name=None)
    rest = ttk if ttk is not None else max(5, pace["pullMedian"] - elapsed)
    return dict(rest=rest, until=rest + pace["gapMedian"], mobs=TYPICAL_MOBS, boss=False, boss_chance=BOSS_SHARE_NO_MAP,
                dur=pace["pullMedian"], src="pacing", next_boss_name=None)


# ---- trash log parsing
def parse_trash(path):
    rows = []
    for line in open(path, encoding="utf-8-sig", errors="replace"):
        p = line.rstrip("\n").split("\t")
        if len(p) < 2: continue
        f = dict(x.split("=", 1) for x in p[2:] if "=" in x)
        rows.append(dict(t=p[0], ev=p[1], f=f, raw=p))
    return rows


def num(v, d=0.0):
    try: return float(re.sub(r"[^0-9.\-]", "", v)) if v not in (None, "", "?") else d
    except ValueError: return d


def clock(t):  # "11:47:26.5" -> seconds of day
    h, m, s = t.split(":"); return int(h) * 3600 + int(m) * 60 + float(s)


def raw_positions(path):
    with gzip.open(path, "rt", encoding="utf-8") as f:
        ev = [json.loads(l) for l in f if l.strip()]
    start = dt.datetime.fromisoformat(ev[0]["local"])
    base = start.hour * 3600 + start.minute * 60 + start.second
    snaps = [(base + e["t"], e["x"], e["z"]) for e in ev if e["e"] == "snap"]
    return snaps


def pos_at(snaps, sec):
    if not snaps: return None
    i = bisect.bisect_left([s[0] for s in snaps], sec)
    i = min(max(i, 0), len(snaps) - 1)
    return (snaps[i][1], snaps[i][2]) if abs(snaps[i][0] - sec) < 3 else None


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--trash", required=True); ap.add_argument("--raw", required=True)
    ap.add_argument("--cactbot", default=os.path.join(HERE, "..", "MitPlanner", "Timelines", "cactbot.zip"))
    ap.add_argument("--learned", default=os.path.join(HERE, "..", "MitPlanner", "Timelines", "Learned"))
    a = ap.parse_args()
    sources = br.timeline_sources(a.cactbot, a.learned)
    raws = sorted(os.path.join(a.raw, f) for f in os.listdir(a.raw) if f.endswith(".jsonl.gz"))
    heads = {}
    for r in raws:
        with gzip.open(r, "rt", encoding="utf-8") as f: heads[r] = json.loads(f.readline())
    totals = dict(presses=0, holds=0)
    out = []
    for tf in sorted(os.listdir(a.trash)):
        if not tf.endswith(".log"): continue
        rows = parse_trash(os.path.join(a.trash, tf))
        zone = next((r for r in rows if r["ev"] == "ZONE"), None)
        if not zone: continue
        zname, job = zone["f"].get("zone"), zone["f"].get("job")
        day = tf[:10]
        # matching recording: same zone + job, started within 5 minutes of the trash log
        t0 = clock(rows[0]["t"])
        match = None
        for r, h in heads.items():
            if h.get("zone") == zname and f'{h.get("job")}{h.get("lvl")}' == job and h.get("local", "").startswith(day):
                st = dt.datetime.fromisoformat(h["local"]); s = st.hour * 3600 + st.minute * 60 + st.second
                if abs(s - t0) < 300: match = r
        others = [r for r, h in heads.items() if h.get("zone") == zname and r != match]
        route, pace = None, dict(gapMedian=17.2, pullMedian=60.5)
        with tempfile.TemporaryDirectory() as tmp:
            if others:
                import contextlib, io
                with contextlib.redirect_stdout(io.StringIO()):
                    br.build(others, tmp, sources)
                files = [f for f in os.listdir(tmp) if f[0].isdigit()]
                if files: route = json.load(open(os.path.join(tmp, files[0])))
        snaps = raw_positions(match) if match else []
        pull_start, last_snap = None, None
        ends = [r for r in rows if r["ev"] == "END"]
        out.append(f"\n## {tf}  (map from {len(others)} other run(s){'' if route else ': none, pacing only'}; positions: {'yes' if snaps else 'no'})")
        for i, r in enumerate(rows):
            # A pull starts at the first non-idle snapshot and lasts until its END line (Trash mode
            # flickers to Idle mid-pull while gathering; the plugin keeps the first start too).
            if r["ev"] == "END":
                pull_start = None
            if r["ev"] == "SNAP":
                if pull_start is None and r["f"].get("state") != "Idle": pull_start = clock(r["t"])
                last_snap = r
            if r["ev"] != "PRESS" or r["raw"][2] not in BIG: continue
            layer = r["f"].get("layer", "")
            f = r["f"]
            hp, inc, net = num(f.get("hp")), num(f.get("dmg")), num(f.get("net"))
            mobs = int(num(str(f.get("mobs", "0/0")).split("/")[0]))
            ttk = num(f.get("ttk"), None) if f.get("ttk") not in (None, "?") else None
            sec = clock(r["t"])
            elapsed = sec - pull_start if pull_start else 0
            nxt = lookahead(route, pace, pos_at(snaps, pull_start) if pull_start else pos_at(snaps, sec), elapsed, ttk)
            now, held, use, why = evaluate(hp, inc, net, mobs, nxt["rest"], 0.4, 15, 120, nxt)
            totals["presses"] += 1
            # what came next: the first pull ending after this one's end
            this_end = next((e for e in ends if clock(e["t"]) >= sec), None)
            later = [e for e in ends if this_end and clock(e["t"]) > clock(this_end["t"]) + 2]
            nxt_pull = later[0] if later else None
            outcome = ""
            if nxt_pull:
                gap = clock(nxt_pull["t"]) - num(nxt_pull["f"].get("duration")) - sec
                outcome = f"next pull {gap:.0f}s later: min HP {nxt_pull['f'].get('minhp')}, {nxt_pull['f'].get('duration')}"
            if not use: totals["holds"] += 1
            flag = "HOLD" if not use else "use "
            if layer == "emergency" and not use: flag = "HOLD?"
            out.append(f"- `{r['t'][:8]}` {r['raw'][2]} [{layer}] hp={hp:.0f}% {inc:.1f}%/s mobs={mobs} -> **{flag}** "
                       f"now={now:.1f} held={held:.1f} ({why}; next={'boss ' + str(nxt['next_boss_name']) if nxt['boss'] else str(nxt['mobs']) + ' mobs'} "
                       f"in {nxt['until']:.0f}s, {nxt['src']}). {outcome}")
    print(f"# Lookahead replay: {totals['presses']} big-mit presses, {totals['holds']} would be held")
    print("\n".join(out))


if __name__ == "__main__":
    main()
