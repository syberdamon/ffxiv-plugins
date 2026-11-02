# ffxiv-plugins

Personal Dalamud plugins (API 15).

| Plugin | What it does |
|---|---|
| [Tank Stance Manager](TankStanceManager/README.md) | MT: stance always on. OT: holds a configurable enmity band of the MT. |

## Install on any PC (one-time)
1. In game: `/xlsettings` > Experimental > Custom Plugin Repositories.
2. Add: `https://raw.githubusercontent.com/syberdamon/ffxiv-plugins/main/repo.json`
3. Tick Enabled, Save. The plugins now show up in `/xlplugins` and auto-update.

## Release a new version
1. Bump `<Version>` in the plugin's .csproj and push to main.
2. Tag it `<PluginFolder>-v<version>` and push the tag, e.g.
   `git tag TankStanceManager-v0.1.1 && git push origin TankStanceManager-v0.1.1`
3. GitHub Actions builds it, publishes a Release, and updates `repo.json`.

## Adding a plugin
See [docs/NEW_PLUGIN.md](docs/NEW_PLUGIN.md).

Note: some plugins here automate in-game actions, which violates Square Enix's
ToS and is not allowed in Dalamud's official repo. Use at your own risk.
