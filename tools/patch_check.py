#!/usr/bin/env python3
"""
Patch-day check: is the official Dalamud release safe to run on a game version it doesn't
support yet? Runs in GitHub Actions (.github/workflows/patch-check.yml); its answer is read by
the patch-day launcher on the PC (tools/patchday/XIVLauncher-PatchDay.ps1).

    python3 tools/patch_check.py --out patch-status.json [--work DIR] [--repo .]

1. Official Dalamud (kamori release track): its version and the game version it supports.
2. The FFXIVClientStructs commit that Dalamud release was built with (its submodule).
3. ClientStructs main: which game version it's updated for (ida/data.yml "version:").
4. Same game version -> nothing to do ("official"). Newer -> compare the two ClientStructs
   commits: function signatures, field offsets and struct sizes that changed, and whether
   Dalamud's own source (at that release tag) or our plugins use any of them.
5. "safe": nothing changed that Dalamud's core uses, and ClientStructs has been quiet for
   SETTLE_MINUTES since the version bump (signatures land after the version: 22 min on 2026-11-10).

Limits: Dalamud's own signatures (outside ClientStructs) can't be checked without the game's
executable; the launcher's crash fallback covers that. Third-party plugins aren't checked.
"""
import argparse, datetime as dt, json, os, re, subprocess, sys, urllib.request

KAMORI = "https://kamori.goats.dev/Dalamud/Release/VersionInfo?track=release&bucket=Control"
DALAMUD = "https://github.com/goatcorp/Dalamud"
CS = "https://github.com/aers/FFXIVClientStructs"
SETTLE_MINUTES = 60


def git(*args, cwd=None):
    return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True).stdout


def official():
    with urllib.request.urlopen(KAMORI, timeout=30) as r:
        info = json.loads(r.read().decode("utf-8"))
    get = lambda k: info.get(k) or info.get(k[0].lower() + k[1:])
    return get("AssemblyVersion"), get("SupportedGameVer")


def clone(url, path):
    if not os.path.isdir(os.path.join(path, ".git")):
        git("clone", "-q", "--filter=blob:none", url, path)
    else:
        git("fetch", "-q", "--tags", "origin", cwd=path)


# ---- light C# parsing: types, signatures, field offsets, struct sizes
TYPE = re.compile(r"\b(?:struct|class|interface)\s+(\w+)")
SIG = re.compile(r"\[(MemberFunction|VirtualFunction|StaticAddress)\(([^)]*)\)\]")
SIZE = re.compile(r"StructLayout\([^)]*Size\s*=\s*(0x[0-9A-Fa-f]+|\d+)")
FIELD = re.compile(r"\[FieldOffset\((0x[0-9A-Fa-f]+|\d+)\)\]\s*(?:\[[^\]]*\]\s*)*public\s+(?:fixed\s+|readonly\s+)?[\w<>*,\s\[\].?]+?\s+(\w+)\s*[;\[=]")
MEMBER = re.compile(r"public\s+(?:static\s+)?(?:partial\s+)?(?:unsafe\s+)?[\w<>*,\[\].? ]+?\s+(\w+)\s*\(")


def parse(text):
    """{(type, kind, name): value}: kind sig (signature), field (offset), size (struct size)."""
    out, cur, pending_size, pending_sig = {}, None, None, None
    for line in text.splitlines():
        s = line.strip()
        if s.startswith("//"):
            continue
        m = SIZE.search(s)
        if m: pending_size = m.group(1)
        t = TYPE.search(s)
        if t and not s.startswith("["):
            cur = t.group(1)
            if pending_size: out[(cur, "size", "")] = pending_size; pending_size = None
        m = SIG.search(s)
        if m: pending_sig = f"{m.group(1)}({m.group(2)})"
        f = FIELD.search(s)
        if f and cur: out[(cur, "field", f.group(2))] = f.group(1)
        mm = MEMBER.search(s)
        if mm and pending_sig and cur:
            out[(cur, "sig", mm.group(1))] = pending_sig; pending_sig = None
        elif s and not s.startswith("[") and pending_sig and "(" in s and cur:
            pending_sig = None
    return out


def changed_items(cs, old, new):
    files = [f for f in git("diff", "--name-only", old, new, "--", "FFXIVClientStructs", cwd=cs).splitlines() if f.endswith(".cs")]
    items = []
    for f in files:
        try: a = parse(git("show", f"{old}:{f}", cwd=cs))
        except subprocess.CalledProcessError: a = {}
        try: b = parse(git("show", f"{new}:{f}", cwd=cs))
        except subprocess.CalledProcessError: b = {}
        for key, val in a.items():
            if b.get(key) != val:  # changed or removed (added members can't affect an older build)
                items.append(dict(type=key[0], kind=key[1], name=key[2], file=f, old=val, new=b.get(key)))
    return items


def uses(src_dirs, item):
    """Files that mention the type and (for members) the member name."""
    hits = []
    word_t = re.compile(rf"\b{re.escape(item['type'])}\b")
    word_m = re.compile(rf"\b{re.escape(item['name'])}\b") if item["name"] else None
    for root in src_dirs:
        for dp, dn, fn in os.walk(root):
            if "/lib/" in dp + "/" or "/.git" in dp or "/obj" in dp or "/bin" in dp:
                continue
            for n in fn:
                if not n.endswith(".cs"): continue
                p = os.path.join(dp, n)
                try: text = open(p, encoding="utf-8", errors="ignore").read()
                except OSError: continue
                if word_t.search(text) and (word_m is None or word_m.search(text)):
                    hits.append(os.path.relpath(p, root))
    return hits


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", required=True)
    ap.add_argument("--work", default="/tmp/patchcheck")
    ap.add_argument("--repo", default=".")
    # Back-testing a past patch day: pretend official Dalamud / ClientStructs / the clock were these.
    ap.add_argument("--official", help="VERSION,GAMEVER instead of asking kamori")
    ap.add_argument("--cs-ref", default="origin/main")
    ap.add_argument("--now", help="ISO time")
    a = ap.parse_args()
    os.makedirs(a.work, exist_ok=True)
    now = dt.datetime.fromisoformat(a.now) if a.now else dt.datetime.now(dt.timezone.utc)
    status = dict(checkedAt=now.isoformat(timespec="seconds"))

    dal_ver, supported = a.official.split(",") if a.official else official()
    status.update(officialDalamud=dal_ver, officialSupportsGame=supported)
    dal, cs = os.path.join(a.work, "dalamud"), os.path.join(a.work, "cs")
    clone(DALAMUD, dal); clone(CS, cs)
    git("checkout", "-q", dal_ver, cwd=dal)
    cs_old = git("ls-tree", dal_ver, "lib/FFXIVClientStructs", cwd=dal).split()[2]
    cs_new = git("rev-parse", a.cs_ref, cwd=cs).strip()
    latest = re.search(r"^version:\s*(\S+)", git("show", f"{cs_new}:ida/data.yml", cwd=cs), re.M).group(1)
    status.update(latestGame=latest, clientStructs=dict(dalamudBuiltWith=cs_old, latest=cs_new))

    if latest == supported:
        status.update(state="official", safe=None, summary=f"Official Dalamud {dal_ver} supports {latest}. Nothing to do.")
    else:
        # When did ClientStructs move to this game version, and has it settled since?
        bump = git("log", "-1", "--format=%cI", "-S", f"version: {latest}", cs_new, "--", "ida/data.yml", cwd=cs).strip()
        last = git("log", "-1", "--format=%cI", cs_new, "--", "FFXIVClientStructs", cwd=cs).strip()
        settled_since = max(dt.datetime.fromisoformat(bump), dt.datetime.fromisoformat(last)) if bump and last else None
        settled = settled_since is not None and (now - settled_since).total_seconds() >= SETTLE_MINUTES * 60
        items = changed_items(cs, cs_old, cs_new)
        dal_src, ours = [os.path.join(dal, "Dalamud")], [a.repo]
        for it in items:
            it["usedByDalamud"] = uses(dal_src, it)[:5]
            it["usedByOurPlugins"] = uses(ours, it)[:5]
        core = [it for it in items if it["usedByDalamud"]]
        status.update(
            clientStructsSettledSince=settled_since.isoformat() if settled_since else None,
            changes=items,
            changesUsedByDalamud=len(core),
            changesUsedByOurPlugins=sum(1 for it in items if it["usedByOurPlugins"]))
        if not settled:
            status.update(state="settling", safe=False,
                          summary=f"Game {latest} is out; ClientStructs is still being updated (waiting {SETTLE_MINUTES} min after its last change).")
        elif core:
            names = ", ".join(sorted({f"{it['type']}.{it['name'] or '(size)'}" for it in core}))[:400]
            status.update(state="unsafe", safe=False,
                          summary=f"Game {latest}: {len(core)} change(s) Dalamud {dal_ver} uses ({names}). Wait for the official update.")
        else:
            status.update(state="safe", safe=True,
                          summary=f"Game {latest}: {len(items)} ClientStructs change(s), none used by Dalamud {dal_ver}'s core. "
                                  f"OK to run it until the official update.")
    with open(a.out, "w") as f:
        json.dump(status, f, indent=1)
    print(status["summary"])


if __name__ == "__main__":
    main()
