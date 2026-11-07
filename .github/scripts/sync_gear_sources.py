#!/usr/bin/env python3
"""
Build RetainerGear/Data/gear_sources.json from Teamcraft's public data (MIT, (c) Flavien Normand,
https://github.com/ffxiv-teamcraft/ffxiv-teamcraft):
  - "dungeon": item id -> instance ids it drops in (instance-sources.json), dungeons only
  - "instances": instance id -> {name, level, ilvl} for those dungeons (instances.json)
  - "shop": item id -> [[currency item id, amount], ...] for special-shop trades paid in a
            single non-gil currency with id < 100 (tomestones and similar; the plugin keeps
            only the currencies you choose, e.g. Poetics and Mathematics)
Usage: sync_gear_sources.py OUT.json [CACHE_DIR]
"""
import json, os, sys, urllib.request

BASE = "https://raw.githubusercontent.com/ffxiv-teamcraft/ffxiv-teamcraft/staging/libs/data/src/lib/json/"
DUNGEON = 2  # ContentType


def load(name, cache):
    path = os.path.join(cache, name) if cache else None
    if path and os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    with urllib.request.urlopen(BASE + name, timeout=120) as r:
        data = r.read()
    if path:
        with open(path, "wb") as f:
            f.write(data)
    return json.loads(data)


def main():
    out = sys.argv[1]
    cache = sys.argv[2] if len(sys.argv) > 2 else None
    instances = load("instances.json", cache)
    sources = load("instance-sources.json", cache)
    shops = load("shops.json", cache)

    dungeons = {int(k): v for k, v in instances.items() if v.get("contentType") == DUNGEON}
    dungeon = {}
    for item, ids in sources.items():
        ids = sorted({i for i in ids if i in dungeons})
        if ids:
            dungeon[int(item)] = ids
    used = {i for ids in dungeon.values() for i in ids}

    shop = {}
    for s in shops:
        if s.get("type") != "SpecialShop":
            continue
        for t in s.get("trades", []):
            cur = t.get("currencies", [])
            items = t.get("items", [])
            if len(cur) != 1 or len(items) != 1 or not (1 < cur[0]["id"] < 100):
                continue
            entry = [cur[0]["id"], cur[0]["amount"]]
            lst = shop.setdefault(items[0]["id"], [])
            if entry not in lst:
                lst.append(entry)

    data = {
        "source": "Teamcraft data (MIT), github.com/ffxiv-teamcraft/ffxiv-teamcraft",
        "dungeon": {str(k): v for k, v in sorted(dungeon.items())},
        "instances": {str(i): {"name": dungeons[i]["en"], "level": dungeons[i].get("levelReq", 0),
                               "ilvl": dungeons[i].get("ilvlReq", 0)} for i in sorted(used)},
        "shop": {str(k): v for k, v in sorted(shop.items())},
    }
    new = json.dumps(data, separators=(",", ":"), sort_keys=True)
    if os.path.exists(out) and open(out, encoding="utf-8").read() == new:
        print("gear sources unchanged")
        return
    with open(out, "w", encoding="utf-8") as f:
        f.write(new)
    print(f"gear sources: {len(dungeon)} dungeon items, {len(used)} dungeons, {len(shop)} shop items")


if __name__ == "__main__":
    main()
