"""Bundle cactbot's fight timelines for Mit Planner.

Usage: python sync_cactbot.py <cactbot checkout> <output zip>

For every cactbot trigger file (ui/raidboss/data/**/<fight>.ts) that names a timeline file
and a zone, this writes into the zip:
  index.json              one entry per zone: territory id, zone key, path, category, tags
  timelines/<path>.txt    the timeline, unmodified
  CACTBOT_LICENSE.txt     cactbot's Apache-2.0 license

Tags (TankBuster / Raidwide / Stack / Mechanic) come from each trigger's response
(Responses.tankBuster(), Responses.aoe(), ...), matched to timeline lines by ability id.
If the output zip already exists and holds exactly the same files, it is left untouched, so
an unchanged cactbot never produces a new plugin release (compressed bytes can differ
between machines; the comparison is on file contents).
"""
import json
import re
import sys
import zipfile
from pathlib import Path

CACTBOT, OUT = Path(sys.argv[1]), Path(sys.argv[2])
DATA = CACTBOT / "ui" / "raidboss" / "data"

EXPANSIONS = {"00-misc": "Misc", "02-arr": "ARR", "03-hw": "HW", "04-sb": "SB",
              "05-shb": "ShB", "06-ew": "EW", "07-dt": "DT"}
TYPES = {"alliance": "Alliance", "dungeon": "Dungeon", "eureka": "Eureka", "raid": "Raid",
         "trial": "Trial", "ultimate": "Ultimate", "deepdungeon": "Deep Dungeon",
         "variant": "Variant", "chaotic": "Chaotic", "criterion": "Criterion", "pvp": "PvP",
         "treasure": "Treasure", "guildhest": "Guildhest", "quest": "Quest"}

# cactbot response helpers -> our mechanic types (priority order: first match wins)
RESPONSE_TYPES = [
    ("SharedTankBuster", r"(?:Responses|Outputs)\.(?:sharedTankBuster|sharedTankbuster|sharedTankBusterOnYou)\b"),
    ("TankBuster", r"(?:Responses|Outputs)\.(?:tankBuster|tankBusterSwap|tankCleave|tankBusterOnYou|tankBusters)\b"),
    ("Raidwide", r"(?:Responses|Outputs)\.(?:aoe|bigAoe|raidwide)\b"),
    ("Stack", r"(?:Responses|Outputs)\.(?:stackMarker|stackMarkerOn|stackOnYou|stack|stackFor)\b"),
    ("Mechanic", r"Responses\.(?:spread|knockback|getUnder|getOut|getIn|getBehind|goFront|moveAway|awayFromFront|lookAway|getUnderOrOut|outOfMelee|goMiddle|interrupt)\b"),
]
PRIORITY = {name: i for i, (name, _) in enumerate(RESPONSE_TYPES)}


def zone_ids():
    text = (CACTBOT / "resources" / "zone_id.ts").read_text(encoding="utf-8", errors="strict")
    return {m.group(1): int(m.group(2)) for m in re.finditer(r"'?(\w+)'?:\s*(\d+),", text)}


def trigger_blocks(ts):
    """Trigger objects in a cactbot file: prettier puts each array item at 4-space indent."""
    return re.findall(r"^    \{\n(.*?)^    \},?$", ts, flags=re.M | re.S)


def ids_in(block):
    m = re.search(r"netRegex:\s*\{(.*?)\}", block, flags=re.S)
    if not m:
        return []
    body = m.group(1)
    one = re.search(r"\bid:\s*'([^']+)'", body)
    many = re.search(r"\bid:\s*\[([^\]]+)\]", body)
    if many:
        return re.findall(r"'([^']+)'", many.group(1))
    return [one.group(1)] if one else []


def trigger_tags(ts):
    """ability-id regex -> mechanic type, from Ability/StartsUsing triggers."""
    tags = []
    for raw, kind in trigger_ids(ts):
        try:
            tags.append((re.compile(f"^(?:{raw})$", re.I), kind))
        except re.error:
            pass
    return tags


def trigger_ids(ts):
    """(raw id, kind) for every Ability/StartsUsing(Extra) trigger with a recognised response."""
    out = []
    for block in trigger_blocks(ts):
        t = re.search(r"\btype:\s*'(\w+)'", block)
        if not t or t.group(1) not in ("StartsUsing", "StartsUsingExtra", "Ability"):
            continue
        kind = next((name for name, rx in RESPONSE_TYPES if re.search(rx, block)), None)
        if kind:
            out.extend((raw, kind) for raw in ids_in(block))
    return out


def head_marker_kinds(ts):
    """
    Head-marker id -> mechanic type, for HeadMarker triggers with a recognised response.
    Ids are literal ('0158') or looked up in the file's marker table (headMarkerData['tankbuster']).
    Fights that compute offset marker ids (getHeadmarkerId / decOffset) are skipped.
    """
    table = {}
    for m in re.finditer(r"^const (\w+) = \{(.*?)^\}", ts, flags=re.M | re.S):
        for k, v in re.findall(r"'?(\w+)'?:\s*'([0-9A-Fa-f]{4})'", m.group(2)):
            table[(m.group(1), k)] = v.upper()
    out = {}
    for block in trigger_blocks(ts):
        t = re.search(r"\btype:\s*'(\w+)'", block)
        if not t or t.group(1) != "HeadMarker":
            continue
        kind = next((name for name, rx in RESPONSE_TYPES if re.search(rx, block)), None)
        if kind not in ("SharedTankBuster", "TankBuster", "Stack", "Raidwide"):
            continue
        net = re.search(r"netRegex:\s*\{(.*?)\}", block, flags=re.S)
        if not net:
            continue
        ids = re.findall(r"\bid:\s*'([0-9A-Fa-f]{4})'", net.group(1))
        ids += [table[(a, k)] for a, k in re.findall(r"\bid:\s*(\w+)\[\s*'(\w+)'\s*\]", net.group(1)) if (a, k) in table]
        ids += [table[(a, k)] for a, k in re.findall(r"\bid:\s*(\w+)\.(\w+)", net.group(1)) if (a, k) in table]
        for i in ids:
            if i.upper() not in out or PRIORITY[kind] < PRIORITY[out[i.upper()]]:
                out[i.upper()] = kind
    return out


LINE_RX = re.compile(r'^\s*(\d+(?:\.\d+)?)\s+"([^"]*)"(.*)$')
SYNC_RX = re.compile(r'\b(?:Ability|StartsUsing)\s*\{([^}]*)\}')


def timeline_tags(txt, tags):
    """label -> mechanic type, for timeline lines whose sync id matches a tagged trigger."""
    out = {}
    for line in txt.splitlines():
        code = re.split(r'#(?=(?:[^"]*"[^"]*")*[^"]*$)', line)[0]  # strip comments outside quotes
        m = LINE_RX.match(code)
        if not m:
            continue
        label, rest = m.group(2), m.group(3)
        if label.startswith("--") and label.endswith("--"):
            continue
        s = SYNC_RX.search(rest)
        if not s:
            continue
        one = re.search(r'\bid:\s*"([^"]+)"', s.group(1))
        many = re.search(r'\bid:\s*\[([^\]]+)\]', s.group(1))
        ids = re.findall(r'"([^"]+)"', many.group(1)) if many else ([one.group(1)] if one else [])
        for pattern, kind in tags:
            if any(pattern.match(i) for i in ids):
                if label not in out or PRIORITY[kind] < PRIORITY[out[label]]:
                    out[label] = kind
    return out


def category(rel):
    parts = rel.parts
    exp = EXPANSIONS.get(parts[0], parts[0])
    kind = TYPES.get(parts[1], parts[1].title()) if len(parts) > 2 else ""
    return f"{exp} {kind}".strip()


def main():
    zones = zone_ids()
    fights = {}
    for ts_path in sorted(DATA.rglob("*.ts")):
        ts = ts_path.read_text(encoding="utf-8", errors="strict")
        tl = re.search(r"timelineFile:\s*'([^']+)'", ts)
        if not tl:
            continue
        txt_path = ts_path.parent / tl.group(1)
        if not txt_path.exists():
            continue
        zm = re.search(r"zoneId:\s*(\[[^\]]*\]|ZoneId\.\w+)", ts)
        if not zm:
            continue
        keys = re.findall(r"ZoneId\.(\w+)", zm.group(1))
        txt = txt_path.read_text(encoding="utf-8", errors="strict")
        rel = txt_path.relative_to(DATA).with_suffix("")
        tags = timeline_tags(txt, trigger_tags(ts))
        # Plain hex ids by kind: the plugin also matches these by ability NAME (via game data),
        # since triggers often fire on the cast id while the timeline syncs on the damage id.
        by_kind = {}
        for raw, kind in trigger_ids(ts):
            if re.fullmatch(r"[0-9A-Fa-f]{1,5}", raw):
                by_kind.setdefault(kind, set()).add(raw.upper())
        triggers = {k: sorted(v) for k, v in sorted(by_kind.items())}
        markers = dict(sorted(head_marker_kinds(ts).items()))
        for key in keys:
            territory = zones.get(key)
            if not territory or territory in fights:
                continue
            fights[territory] = {
                "territory": territory,
                "zoneKey": key,
                "path": rel.as_posix(),
                "category": category(rel),
                "tags": dict(sorted(tags.items())),
                "triggerIds": triggers,
                "headMarkers": markers,
                "_text": txt,
            }

    index = [{k: v for k, v in f.items() if k != "_text"} for f in sorted(fights.values(), key=lambda f: f["territory"])]
    files = {"index.json": json.dumps(index, indent=1, ensure_ascii=False)}
    for f in sorted(fights.values(), key=lambda f: f["path"]):
        files[f"timelines/{f['path']}.txt"] = f["_text"]
    files["CACTBOT_LICENSE.txt"] = (CACTBOT / "LICENSE").read_text(encoding="utf-8", errors="strict")

    if OUT.exists():
        with zipfile.ZipFile(OUT) as old:
            if {n: old.read(n).decode("utf-8") for n in old.namelist()} == files:
                print(f"{len(index)} fights: unchanged, {OUT} left as is")
                return

    OUT.parent.mkdir(parents=True, exist_ok=True)
    stamp = (2020, 1, 1, 0, 0, 0)

    def put(z, name, data):
        info = zipfile.ZipInfo(name, date_time=stamp)
        info.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(info, data)

    with zipfile.ZipFile(OUT, "w") as z:
        for name, text in files.items():
            put(z, name, text)

    tagged = sum(1 for f in index if f["tags"])
    print(f"{len(index)} fights ({tagged} with tags) -> {OUT} ({OUT.stat().st_size / 1024:.0f} KB)")


if __name__ == "__main__":
    main()
