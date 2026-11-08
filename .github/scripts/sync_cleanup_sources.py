#!/usr/bin/env python3
"""
Build MaterialStash/Data/cleanup_sources.json from Teamcraft's public data (MIT, (c) Flavien Normand,
https://github.com/ffxiv-teamcraft/ffxiv-teamcraft). Material Stash's Clean-up tab uses it to say
where an item comes from (so you know what getting it again takes) and what exchange tokens buy:
  - "instances": instance id -> [name, content type, level] for every duty some item drops in
  - "duties":    item id -> instance ids it drops in (instance-sources.json; bad ids dropped)
  - "gil":       item id -> lowest price at a gil shop (shops.json, GilShop)
  - "shops":     item id -> [currency item id, amount] of its cheapest single-currency special-shop
                 trade (tomestones, seals, tokens, totems...)
  - "tokens":    currency item id (>= 100, i.e. a real item, not tomestones/seals) -> item ids it buys
Usage: sync_cleanup_sources.py OUT.json [CACHE_DIR]
"""
import json, os, sys, urllib.request

BASE = "https://raw.githubusercontent.com/ffxiv-teamcraft/ffxiv-teamcraft/staging/libs/data/src/lib/json/"
MAX_REWARDS = 60


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

    duties, used = {}, set()
    for item, ids in sources.items():
        good = sorted({i for i in ids if isinstance(i, int) and i > 0 and str(i) in instances})
        if good:
            duties[int(item)] = good
            used.update(good)
    inst = {}
    for i in sorted(used):
        v = instances[str(i)]
        inst[i] = [v.get("en", ""), v.get("contentType", 0), v.get("levelReq", 0)]

    gil, shop, tokens = {}, {}, {}
    for s in shops:
        kind = s.get("type")
        for t in s.get("trades", []):
            cur = t.get("currencies", [])
            for it in t.get("items", []):
                iid = it.get("id")
                if not isinstance(iid, int) or iid <= 0:
                    continue
                if kind == "GilShop" and len(cur) == 1 and cur[0].get("id") == 1:
                    p = cur[0].get("amount", 0)
                    if p > 0 and (iid not in gil or p < gil[iid]):
                        gil[iid] = p
                elif kind == "SpecialShop" and len(cur) == 1:
                    c, a = cur[0].get("id"), cur[0].get("amount", 0)
                    if not isinstance(c, int) or c <= 1 or a <= 0 or c == iid:
                        continue
                    if iid not in shop or a < shop[iid][1]:
                        shop[iid] = [c, a]
                    if c >= 100:
                        lst = tokens.setdefault(c, [])
                        if iid not in lst and len(lst) < MAX_REWARDS:
                            lst.append(iid)

    data = {
        "instances": {str(k): v for k, v in inst.items()},
        "duties": {str(k): v for k, v in sorted(duties.items())},
        "gil": {str(k): v for k, v in sorted(gil.items())},
        "shops": {str(k): v for k, v in sorted(shop.items())},
        "tokens": {str(k): v for k, v in sorted(tokens.items())},
    }
    os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
    with open(out, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, separators=(",", ":"), sort_keys=False)
        f.write("\n")
    print(f"{out}: {len(inst)} duties, {len(duties)} items with duty drops, {len(gil)} gil-shop items, "
          f"{len(shop)} special-shop items, {len(tokens)} tokens")


if __name__ == "__main__":
    main()
