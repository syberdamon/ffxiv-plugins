"""Add or update one plugin's entry in repo.json (the Dalamud custom repo file).

Usage: python update_repo_json.py <PluginFolder> <tag>
Reads the manifest DalamudPackager writes next to the built DLL, then points the
download links at the GitHub Release created for <tag>.
"""
import json
import sys
from pathlib import Path

OWNER_REPO = "syberdamon/ffxiv-plugins"

plugin, tag = sys.argv[1], sys.argv[2]
# DalamudPackager writes <plugin>.json next to latest.zip; find it wherever it landed.
zip_path = next((Path(plugin) / "bin").rglob("latest.zip"))
manifest = json.loads((zip_path.parent / f"{plugin}.json").read_text(encoding="utf-8-sig"))

link = f"https://github.com/{OWNER_REPO}/releases/download/{tag}/{plugin}.zip"
manifest.update({
    "DownloadLinkInstall": link,
    "DownloadLinkUpdate": link,
    "DownloadLinkTesting": link,
    "RepoUrl": f"https://github.com/{OWNER_REPO}",
})

repo_path = Path("repo.json")
entries = json.loads(repo_path.read_text(encoding="utf-8")) if repo_path.exists() else []
entries = [e for e in entries if e.get("InternalName") != manifest.get("InternalName")]
entries.append(manifest)
entries.sort(key=lambda e: e.get("InternalName", ""))

repo_path.write_text(json.dumps(entries, indent=2) + "\n", encoding="utf-8")
print(f"repo.json: {manifest.get('InternalName')} {manifest.get('AssemblyVersion')} -> {link}")
