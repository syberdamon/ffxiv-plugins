"""Healer review: for each tank buster / raidwide hit on a boss, which of my shields/mits went out before it.
Usage: python3 healer_review.py LOG.jsonl.gz [...]
Buster: one tank hit for >= 25% of max HP by a boss ability (not an auto-attack).
Raidwide: >= 75% of the party hit for >= 10% max HP each within 1 s.
"""
import gzip, json, sys, statistics
from collections import defaultdict

TANKS = {"PLD", "WAR", "DRK", "GNB", "GLA", "MRD"}
# Healer tools (same IDs as MitPlanner/HealerKit.cs), plus a few others worth seeing.
BUSTER = {7432: "Divine Benison", 25861: "Aquaveil", 25867: "Protraction", 7434: "Excogitation",
          185: "Adloquium", 25873: "Exaltation", 16556: "Celestial Intersection", 3595: "Aspected Benefic",
          24305: "Haima", 24303: "Taurochole", 24291: "Eukrasian Diagnosis", 24317: "Krasis", 7433: "Plenary"}
RAID = {16536: "Temperance", 25862: "Liturgy of the Bell", 3569: "Asylum", 188: "Sacred Soil", 25868: "Expedient",
        16538: "Fey Illumination", 186: "Succor", 37013: "Concitation", 3613: "Collective Unconscious",
        7439: "Earthly Star", 3601: "Aspected Helios", 37030: "Helios Conjunction", 24298: "Kerachole",
        24302: "Physis II", 24310: "Holos", 24311: "Panhaima", 24292: "Eukrasian Prognosis",
        37034: "Eukrasian Prognosis II", 16559: "Neutral Sect"}


def load(path):
    with gzip.open(path, "rt", encoding="utf-8") as f:
        return [json.loads(l) for l in f if l.strip()]


def review(path):
    ev = load(path)
    head = ev[0]
    party = {p["slot"]: p["job"] for p in head["party"]}
    tanks = {s for s, j in party.items() if j in TANKS}
    names, mhp_e, mhp_p = {}, {}, {}
    myuses = []  # (t, id)
    hits = []    # (t, src, act, target, dmg)
    deaths = []
    for e in ev:
        k = e["e"]
        if k == "def" and "act" in e: names[e["act"]] = e["name"]
        elif k == "ent": mhp_e[e["who"]] = e["mhp"]
        elif k == "snap":
            mhp_p["me"] = e["mhp"]
            for p in e.get("party", []): mhp_p[p["who"]] = p["mhp"]
        elif k == "act":
            if e["who"] == "me" and e["act"] in {**BUSTER, **RAID}: myuses.append((e["t"], e["act"]))
            if e["who"].startswith("e"):
                for h in e["hits"]:
                    d = sum(x.get("v", 0) for x in h["fx"] if x.get("k") == "dmg")
                    if d > 0: hits.append((e["t"], e["who"], e["act"], h["to"], d))
        elif k == "death" and not e["who"].startswith("e"): deaths.append((e["t"], e["who"], party.get(e["who"], "?")))
    mymax = mhp_p.get("me", 1)
    bosses = {w for w, m in mhp_e.items() if m >= 8 * mymax}
    n = len(party)
    out = [f"## {path.split('/')[-1]}  (party {' '.join(party[s] for s in sorted(party))}; bosses: {len(bosses)})"]
    events = []
    # busters
    for t, src, act, to, d in hits:
        if src in bosses and to in tanks and names.get(act, "") not in ("attack", "") and d >= 0.25 * mhp_p.get(to, 1e12):
            events.append((t, "Buster", names.get(act, act), to))
    # raidwides: group by (src, act) within 1 s
    by = defaultdict(list)
    for t, src, act, to, d in hits:
        if src in bosses and d >= 0.10 * mhp_p.get(to, 1e12): by[(act, round(t))].append((t, to))
    seen = set()
    for (act, _), lst in sorted(by.items(), key=lambda x: x[1][0][0]):
        targets = {to for _, to in lst}
        t0 = min(t for t, _ in lst)
        if len(targets) >= 0.75 * n and not any(abs(t0 - s) < 2 and a == act for a, s in seen):
            seen.add((act, t0)); events.append((t0, "Raidwide", names.get(act, act), "party"))
    events.sort()
    # dedupe busters within 2 s of same name
    clean = []
    for e in events:
        if clean and e[1] == clean[-1][1] and e[2] == clean[-1][2] and e[0] - clean[-1][0] < 2: continue
        clean.append(e)
    leads = defaultdict(list)
    covered = defaultdict(int)
    total = defaultdict(int)
    rows = []
    for t, kind, name, to in clean:
        pool = BUSTER if kind == "Buster" else RAID
        used = [(t - u, pool[a]) for u, a in myuses if a in pool and 0 <= t - u <= 15]
        total[kind] += 1
        if used: covered[kind] += 1; leads[kind] += [l for l, _ in used]
        rows.append(f"| {t:7.1f} | {kind} | {name} | {to} | " + (", ".join(f"{nm} {l:.1f}s" for l, nm in used) or "**none**") + " |")
    out.append("| t | kind | ability | target | my shields/mits in the 15 s before (lead) |")
    out.append("|---|---|---|---|---|")
    out += rows
    for kind in ("Buster", "Raidwide"):
        if total[kind]:
            ls = leads[kind]
            out.append(f"- {kind}: {covered[kind]}/{total[kind]} covered" +
                       (f"; lead median {statistics.median(ls):.1f}s, range {min(ls):.1f}-{max(ls):.1f}s" if ls else ""))
    used_all = defaultdict(int)
    for _, a in myuses: used_all[{**BUSTER, **RAID}[a]] += 1
    out.append("- Tools used all run: " + ", ".join(f"{k} x{v}" for k, v in sorted(used_all.items(), key=lambda x: -x[1])))
    if deaths: out.append("- Deaths: " + ", ".join(f"{w}({j}) at {t:.0f}s" for t, w, j in deaths))
    return "\n".join(out)


if __name__ == "__main__":
    print("\n\n".join(review(p) for p in sys.argv[1:]))
