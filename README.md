# ffxiv-plugins

Personal Dalamud plugins (API 15).

| Plugin | What it does |
|---|---|
| [Cooldown Grid](CooldownGrid/README.md) | Clickable cooldown windows: Defensive (self), Defensive (group), Support, OT Sent Mits (casts on the co-tank), plus custom groups. |
| [Map Click Nav](MapClickNav/README.md) | Ctrl+click any map: teleports if faster, mounts, and flies there with vnavmesh. |
| [Combat Recorder](CombatRecorder/README.md) | Records fights (actions, damage/heals, buffs, HP, positions) to local files for review; `/crec mark` to flag moments. |
| [Materia Manager](MateriaManager/README.md) | Auto-stores materia to saddlebag or retainer when you open them; plans transmutation batches with keep-at-least rules and takes you to Mutamix. |
| [Material Stash](MaterialStash/README.md) | One button above the retainer list puts gathered and mob-dropped crafting mats and crystals on your retainers, in the order you choose per category. Also tidies, keeps stock through ventures, and audits retainer junk (Clean-up tab). |
| [Mit Planner](MitPlanner/README.md) | Boss timeline with countdowns; per-job MT/OT cooldown plans with callouts, starter-plan generator, optional auto-use. |
| [Turn-In Manager](TurnInManager/README.md) | Grand Company expert delivery with guardrails (gearsets, ilvl, rarity, HQ/NQ), materia retrieval first, and seal spending up to stock targets. |
| [Poetics Spender](PoeticsSpender/README.md) | One click turns all your Poetics into Grade 3 topsoil (Idyllshire); movable near-cap alert. Needs vnavmesh. |
| [Raise Callouts](RaiseCallouts/README.md) | Says "Raising <name>" in party chat when you raise someone, however it was cast (works with RotationSolver Reborn). |
| [Rescue Helper](RescueHelper/README.md) | Healers: party panel showing who's in an AoE about to go off (from BossMod); click to Rescue, or automatic. |
| [Retainer Gear](RetainerGear/README.md) | Tracks retainer gear; lists the best non-raid upgrades (crafted, dungeon, uncapped tomestones) per retainer, plus a shopping list with Universalis prices. |
| [Tank Stance Manager](TankStanceManager/README.md) | MT: stance always on. OT: holds a configurable enmity band of the MT. |

## Install on any PC (one-time)
1. In game: `/xlsettings` > Experimental > Custom Plugin Repositories.
2. Add: `https://raw.githubusercontent.com/syberdamon/ffxiv-plugins/main/repo.json`
3. Tick Enabled, Save. The plugins now show up in `/xlplugins` and auto-update.

## Release a new version
1. Bump `<Version>` in the plugin's .csproj (e.g. 0.1.0.0 -> 0.1.1.0).
2. Push to main. GitHub Actions builds it, publishes a Release, and updates
   `repo.json`. Dalamud picks up the update on each PC.

## Adding a plugin
See [docs/NEW_PLUGIN.md](docs/NEW_PLUGIN.md).

Credits: see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Note: some plugins here automate in-game actions, which violates Square Enix's
ToS and is not allowed in Dalamud's official repo. Use at your own risk.
