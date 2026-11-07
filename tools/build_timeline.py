#!/usr/bin/env python3
"""
Build cactbot-format boss timelines from Combat Recorder logs, for fights cactbot has no timeline
for (and, for checking the tool, ones it does).

    python3 tools/build_timeline.py LOG.jsonl.gz [LOG2.jsonl.gz ...] --out DIR [--min-kills 2]

All logs must be from the same zone. For each boss (an enemy with >= 8x your max HP, the same
rule Mit Planner's Trash mode uses) and each pull of it:
  - time 0 = the first action that involves the boss;
  - every enemy ability in the pull is collected (auto-attacks dropped), multi-hit/multi-target
    hits within 0.6 s are folded into one line;
  - the n-th use of each ability is lined up across pulls and its time is the median.
Lines are classified from what they did to the party:
  - Raidwide: hit >= 75% of the party for >= 10% of their max HP on average
  - TankBuster: hit one tank for >= 25% of max HP (and was cast or is not a regular auto)
  - SharedTankBuster: hit exactly the tanks (2) for >= 25% max HP each
  - Mechanic: anything else the boss cast; Other: the rest
A repeating stretch at the end (>= 4 abilities in the same order with the same spacing) becomes a
loop (label + forcejump), the way cactbot writes loops.

Writes, per zone:
  <out>/<territory>.txt     the timeline (bosses at 1000, 2000, ...)
  <out>/<territory>.json    index entry for Mit Planner: territory, zone, tags, trigger ids,
                            kills per boss, "trusted" (every boss has >= --min-kills kills)
  <out>/<territory>.md      a review report: per ability, uses, timing spread, who it hit, damage
"""
import argparse, gzip, json, statistics, sys, os
from collections import defaultdict

TANKS = {"PLD", "WAR", "DRK", "GNB", "GLA", "MRD"}
BOSS_HP_FACTOR = 8.0
FOLD_WINDOW = 0.6


def read(path):
    op = gzip.open if path.endswith(".gz") else open
    with op(path, "rt", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    yield json.loads(line)
                except json.JSONDecodeError:
                    pass  # a crash can leave a half line at the end


class Log:
    def __init__(self, path):
        self.path = path
        self.events = list(read(path))
        head = next((e for e in self.events if e["e"] == "head"), {})
        self.zone = head.get("zone", "?")
        self.territory = head.get("territory", 0)
        self.party = {p["slot"]: p["job"] for p in head.get("party", [])}
        if "me" not in self.party and head.get("job"):
            self.party["me"] = head["job"]
        self.names = {}       # action id -> name
        self.ents = {}        # enemy who -> (name, max hp)
        self.mhp = {}         # party who -> max hp
        for e in self.events:
            k = e["e"]
            if k == "def" and "act" in e:
                self.names[e["act"]] = e.get("name", "")
            elif k == "ent":
                self.ents[e["who"]] = (e.get("name", "?"), e.get("mhp", 0))
            elif k == "snap":
                if e.get("mhp"):
                    self.mhp["me"] = max(self.mhp.get("me", 0), e["mhp"])
                for p in e.get("party", []):
                    if p.get("mhp"):
                        self.mhp[p["who"]] = max(self.mhp.get(p["who"], 0), p["mhp"])

    def segments(self):
        """Combat on/off stretches as (start, end, [events])."""
        out, cur, start = [], None, 0
        for e in self.events:
            if e["e"] == "combat":
                if e.get("on") and cur is None:
                    cur, start = [], e["t"]
                elif not e.get("on") and cur is not None:
                    out.append((start, e["t"], cur)); cur = None
            elif cur is not None:
                cur.append(e)
        if cur is not None:
            out.append((start, self.events[-1]["t"], cur))
        return out


def is_auto(name):
    return name.strip().lower() in ("", "attack", "auto-attack", "shot")


def pulls(log):
    """Yield (boss name, killed, [ability uses]) per boss pull."""
    # Compare against the sturdiest party member (a tank), so a healer's log doesn't turn big trash
    # into "bosses".
    my_mhp = max(log.mhp.values(), default=0) or 1
    deaths = {}
    for e in log.events:
        if e["e"] == "death" and str(e.get("who", "")).startswith("e"):
            deaths.setdefault(e["who"], e["t"])
    for start, end, evs in log.segments():
        enemies = {e["who"] for e in evs if e["e"] in ("act", "cast") and str(e.get("who", "")).startswith("e")}
        for e in evs:
            if e["e"] == "act":
                for h in e.get("hits", []):
                    if str(h.get("to", "")).startswith("e"):
                        enemies.add(h["to"])
        big = [w for w in enemies if log.ents.get(w, ("", 0))[1] >= BOSS_HP_FACTOR * my_mhp]
        # Several boss-sized enemies in one fight: the biggest is the boss, the rest are its adds
        # (their abilities still land in the boss's timeline). Same-HP pairs both stay.
        top = max((log.ents[w][1] for w in big), default=0)
        bosses = [w for w in big if log.ents[w][1] >= 0.9 * top]
        for boss in bosses:
            name = log.ents[boss][0]
            t0 = None
            for e in evs:
                if e["e"] == "act" and (e["who"] == boss or any(h.get("to") == boss for h in e.get("hits", []))):
                    t0 = e["t"]; break
            if t0 is None:
                continue
            # Died during the pull, or just after combat dropped (the death can land after "combat off").
            killed = boss in deaths and t0 <= deaths[boss] <= end + 5
            casts = {}  # (who, act) -> cast start
            last_cast = None  # (t of the latest cast's resolution, its target)
            uses = []
            for e in evs:
                if e["t"] < t0 - 1:
                    continue
                if e["e"] == "cast" and str(e.get("who", "")).startswith("e"):
                    casts[(e["who"], e["act"])] = (e["t"], e.get("time", 0), e.get("tgt", ""))
                if e["e"] != "act" or not str(e.get("who", "")).startswith("e"):
                    continue
                act = e["act"]
                aname = log.names.get(act, "")
                cast = casts.pop((e["who"], act), None)
                src = log.ents.get(e["who"], ("?", 0))[0]
                t = e["t"] - t0
                if is_auto(aname) and cast is None:
                    continue
                hits = []
                for h in e.get("hits", []):
                    to = h.get("to", "")
                    if to in log.party:
                        dmg = sum(fx.get("v", 0) for fx in h.get("fx", []) if fx.get("k") == "dmg")
                        hits.append((to, dmg))
                tgt = cast[2] if cast else ""
                paired = False
                if cast is None:
                    # A follow-up of a cast that just resolved (the damage part of a two-id spell, often
                    # dealt by invisible helper actors, one per target)?
                    # Same-named source (helpers carry the boss's name), and either a helper actor or
                    # the same ability name (rules out the boss's own auto-attacks).
                    if (last_cast and 0 <= t - last_cast[0] <= 2.0 and src == last_cast[2]
                            and (e["who"] != last_cast[3] or aname == last_cast[4])):
                        paired, tgt = True, last_cast[1]
                else:
                    last_cast = (t, tgt, src, e["who"], aname)
                uses.append(dict(t=t, act=act, name=aname or f"unknown_{act:X}", src=src,
                                 cast=cast is not None, paired=paired, tgt=tgt, casttime=cast[1] if cast else 0, hits=hits))
            yield name, killed, [u for u in fold(uses) if keep(u)], end - t0


def keep(u):
    """Casts and their follow-ups stay; an un-cast ability only if it hit 2+ party members
    (unannounced raidwide). Drops autos, add filler and healer bells."""
    if u["cast"] or u["paired"]:
        return True
    return len({to for to, d in u["hits"] if d > 0}) >= 2


def fold(uses):
    """Same ability within FOLD_WINDOW = one line (multi-hit / multi-target / several sources)."""
    out = []
    for u in sorted(uses, key=lambda u: u["t"]):
        prev = next((p for p in reversed(out) if p["act"] == u["act"] and u["t"] - p["t"] <= FOLD_WINDOW), None)
        if prev:
            prev["hits"] += u["hits"]; prev["cast"] |= u["cast"]; prev["paired"] |= u["paired"]
        else:
            out.append(dict(u))
    return out


def classify(uses, party, mhp):
    """MechanicType for an ability, from every use of it. Damage on tanks is unreliable (they
    mitigate), so tank busters are recognised by shape: a cast that hits only a tank."""
    n_party = max(1, len(party))
    tanks = {w for w, j in party.items() if j in TANKS}
    frac_hit, tank_only, tank_pair, on_player, raid_dmg, any_cast = [], 0, 0, 0, [], False
    for u in uses:
        any_cast |= u["cast"] or u.get("paired", False)
        targets = defaultdict(int)
        for to, d in u["hits"]:
            targets[to] += d
        frac_hit.append(len(targets) / n_party)
        rel = {to: d / max(1, mhp.get(to, 1)) for to, d in targets.items()}
        if rel:
            raid_dmg.append(statistics.mean(rel.values()))
        if u.get("tgt") in party:
            on_player += 1
        if len(targets) == 1 and next(iter(targets)) in tanks:
            tank_only += 1
        if len(targets) == 2 and set(targets) <= tanks:
            tank_pair += 1
    n = len(uses)
    half = max(1, (n + 1) // 2)
    if statistics.mean(frac_hit) >= 0.75 and raid_dmg and statistics.mean(raid_dmg) >= 0.10:
        return "Stack" if on_player >= half else "Raidwide"
    if any_cast and tank_pair >= half:
        return "SharedTankBuster"
    if any_cast and tank_only >= half:
        return "TankBuster"
    return "Mechanic" if any_cast else "Other"


def merge(boss_pulls):
    """Line up the n-th use of each ability across pulls; median time; drop one-off noise."""
    by_key = defaultdict(list)    # (act, k) -> [use per pull]
    lengths = [p[3] for p in boss_pulls]
    for _, _, uses, _ in boss_pulls:
        count = defaultdict(int)
        for u in uses:
            by_key[(u["act"], count[u["act"]])].append(u)
            count[u["act"]] += 1
    lines = []
    for (act, k), us in by_key.items():
        t = statistics.median(u["t"] for u in us)
        long_enough = sum(1 for L in lengths if L >= t + 2)
        if long_enough >= 2 and len(us) < 2:
            continue   # seen once although other pulls lasted long enough to see it: noise
        lines.append(dict(t=t, act=act, k=k, name=us[0]["name"], src=us[0]["src"],
                          cast=any(u["cast"] for u in us), paired=any(u.get("paired") for u in us), casttime=max(u["casttime"] for u in us),
                          spread=(max(u["t"] for u in us) - min(u["t"] for u in us)) if len(us) > 1 else 0,
                          seen=len(us)))
    lines.sort(key=lambda l: l["t"])
    return lines


def find_loop(lines, min_len=4, tol=1.5):
    """(start index, repeat index) of a repeating tail, or None."""
    ids = [l["act"] for l in lines]
    n = len(ids)
    for i in range(n):
        for j in range(i + min_len, n - min_len + 1):
            L = 0
            while j + L < n and ids[i + L] == ids[j + L]:
                if L > 0:
                    d1 = lines[i + L]["t"] - lines[i + L - 1]["t"]
                    d2 = lines[j + L]["t"] - lines[j + L - 1]["t"]
                    if abs(d1 - d2) > tol:
                        break
                L += 1
                if i + L >= j:
                    break
            if L >= min_len and j + L >= n - 1:
                return i, j
    return None


def write(logs, out, min_kills):
    zone, territory = logs[0].zone, logs[0].territory
    by_boss = defaultdict(list)
    party, mhp = {}, {}
    for log in logs:
        party.update(log.party); mhp.update(log.mhp)
        for name, killed, uses, length in pulls(log):
            if uses:
                by_boss[name].append((name, killed, uses, length))

    txt = [f"### {zone.upper()} (learned)", f"# ZoneId: {territory}",
           f"# Built by tools/build_timeline.py from {len(logs)} Combat Recorder log(s). Review before trusting.",
           "", 'hideall "--sync--"', ""]
    report = [f"# Learned timeline review: {zone} ({territory})", ""]
    tags, trigger_ids, kills = {}, defaultdict(set), {}
    for b, (boss, bp) in enumerate(by_boss.items(), start=1):
        base = 1000.0 * b
        kills[boss] = sum(1 for p in bp if p[1])
        lines = merge(bp)
        kinds = {}
        for act in {l["act"] for l in lines}:
            uses = [u for p in bp for u in p[2] if u["act"] == act]
            kinds[act] = classify(uses, party, mhp)
        loop = find_loop(lines)
        txt += [f"#~~~ {boss} ~~~#", f"# pulls: {len(bp)}, kills: {kills[boss]}", ""]
        report += [f"## {boss}: {len(bp)} pull(s), {kills[boss]} kill(s)" + (f", loop from line {loop[0]}" if loop else ""), "",
                   "| t | id | ability | type | cast | seen | spread s | party hit | avg dmg % max HP |", "|---|---|---|---|---|---|---|---|---|"]
        first = lines[0] if lines else None
        if first and first["cast"]:
            # Sync on the cast bar of the opener, from anywhere earlier in the zone (wide window back).
            t_first = base + first["t"] - first["casttime"]
            txt.append(f'{t_first:.1f} "--sync--" StartsUsing {{ id: "{first["act"]:X}", source: "{first["src"]}" }} window {t_first:.0f},5')
        end = len(lines) if not loop else loop[1] + 1
        for idx, l in enumerate(lines[:end]):
            if loop and idx == loop[0]:
                txt.append(f'{base + l["t"]:.1f} label "{boss.lower().replace(" ", "-")}-loop"')
            label = l["name"] if kinds[l["act"]] not in ("Other",) and (l["cast"] or kinds[l["act"]] != "Mechanic") else "--sync--"
            line = f'{base + l["t"]:.1f} "{label}" Ability {{ id: "{l["act"]:X}", source: "{l["src"]}" }}'
            if idx == 0 and not (first and first["cast"]):
                line += f' window {base + l["t"]:.0f},5'
            if loop and idx == loop[1]:
                line += f' forcejump "{boss.lower().replace(" ", "-")}-loop"'
            txt.append(line)
            if label != "--sync--":
                k = kinds[l["act"]]
                prev = tags.get(label)
                order = ["Other", "Mechanic", "Stack", "Raidwide", "TankBuster", "SharedTankBuster"]
                if prev is None or order.index(k) > order.index(prev):
                    tags[label] = k
                if k != "Other":
                    trigger_ids[k].add(f'{l["act"]:X}')
        txt.append("")
        for l in lines:
            uses = [u for p in bp for u in p[2] if u["act"] == l["act"]]
            hit = statistics.mean(len({h[0] for h in u["hits"]}) for u in uses)
            dmgs = [d / max(1, mhp.get(to, 1)) for u in uses for to, d in u["hits"]]
            report.append(f'| {l["t"]:.1f} | {l["act"]:X} | {l["name"]} ({l["src"]}) | {kinds[l["act"]]} | '
                          f'{"%.1fs" % l["casttime"] if l["cast"] else "-"} | {l["seen"]}/{len(bp)} | {l["spread"]:.1f} | '
                          f'{hit:.1f}/{len(party)} | {100 * statistics.mean(dmgs) if dmgs else 0:.0f} |')
        report.append("")

    trusted = bool(kills) and all(k >= min_kills for k in kills.values())
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, f"{territory}.txt"), "w") as f:
        f.write("\n".join(txt) + "\n")
    with open(os.path.join(out, f"{territory}.json"), "w") as f:
        json.dump(dict(territory=territory, zoneKey=zone, path=f"learned/{territory}", category="Learned",
                       tags=tags, triggerIds={k: sorted(v) for k, v in trigger_ids.items()}, headMarkers={},
                       kills=kills, trusted=trusted), f, indent=1)
    with open(os.path.join(out, f"{territory}.md"), "w") as f:
        f.write("\n".join(report) + f"\nTrusted (every boss >= {min_kills} kills): {trusted}\n")
    print(f"{zone} ({territory}): {len(by_boss)} boss(es), kills {kills}, trusted={trusted} -> {out}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("logs", nargs="+")
    ap.add_argument("--out", required=True)
    ap.add_argument("--min-kills", type=int, default=2)
    a = ap.parse_args()
    logs = [Log(p) for p in a.logs]
    zones = {l.territory for l in logs}
    if len(zones) != 1:
        sys.exit(f"logs are from different zones: {zones}")
    write(logs, a.out, a.min_kills)


if __name__ == "__main__":
    main()
