# Adding a new plugin

1. Create a folder `<PluginName>/` at the repo root with `<PluginName>.csproj`
   (copy TankStanceManager's and edit Name, Punchline, Description, Tags, Version).
2. Add it to `ffxiv-plugins.slnx`.
3. Add a row to the table in the root README.
4. Push to main and confirm the GitHub Actions build passes.
5. The first green build on main publishes it automatically (see root README).

## Reusable prompt for Claude
Paste and fill in:

> New Dalamud plugin for my ffxiv-plugins repo (syberdamon/ffxiv-plugins).
> Name: ___
> What it should do: ___
> When it should be active (combat / duties / always): ___
> Settings I want to control in the UI: ___
> Does it press actions for me, or only display info? ___
> Follow the repo conventions in docs/NEW_PLUGIN.md, and get the
> GitHub Actions build passing (a push to main publishes the release).
