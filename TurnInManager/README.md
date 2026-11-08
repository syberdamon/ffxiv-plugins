# Turn-In Manager

Turn-ins with guardrails. Modules: **Grand Company expert delivery** with seal spending, and
**Scrips** (collectables turn-in + scrip exchange). Planned: Poetics (folding in Poetics Spender).

Commands: `/turnin` (window), `/turnin gc` (run), `/turnin buy` (seal shop only),
`/turnin strip` (retrieve materia only), `/turnin scrips <count> <item>` (turn in collectables, buy the item), `/turnin stop`, `/turnin dump <window>` (writes a game window's data to /xllog, for fixing).

Requires **vnavmesh**. **Lifestream** is optional: with it, the run travels to your Grand
Company (`/li gc`); without it, start the run at your GC. English client.

## Grand Company run
1. **Retrieve materia** from every piece of gear that will be turned in (gear turned in
   with materia loses it). Uses the item's right-click "Retrieve Materia", one materia at a time.
2. **Travel** to your GC and walk to the Personnel Officer.
3. **Expert Delivery:** hands in allowed gear one item at a time. Each delivery is checked: the
   item must leave its slot and seals must go up.
4. **Seal cap:** before a delivery would push seals over the cap, walks to the Quartermaster, buys
   your purchase list (each item up to its target, e.g. Ventures up to 65,000), then returns
   and keeps delivering. Seal Sweetener bonuses are counted.
5. Optional: spend down to a reserve at the end.

**Strip materia only** (button or `/turnin strip`): retrieves materia from every item that passes
your filters, then stops. Works anywhere; no travel.

## Guardrails (what may be turned in)
- **Never items in a gearset** (on by default).
- **Max item level.**
- **Rarity:** green / blue. **Quality:** NQ / HQ.
- **Armoury chest:** include or not.
- **Protected items:** right-click any row in the preview, "Never turn this in".
- Only gear the game itself lists under Expert Delivery can be turned in. Supply/provisioning and
  bonus rows are skipped.

The **Preview** lists exactly what would be offered, what has materia to retrieve, and (optionally)
everything blocked and why.

## Scrips run (Scrips tab, `/turnin scrips 20 Mason's Abrasive`, or Material Stash's scrip goals)
1. **Travel** to your scrip hub (default Solution Nine, Nexus Arcade shard; or Old Gridania): Lifestream teleports
   and takes the aethernet, unless the NPCs are already in sight.
2. **Collectable Appraiser:** turns in every collectable in your bags that pays the item's scrip (picking the job
   tab and the item, one at a time). Each turn-in is checked: scrips must go up.
3. **Before the cap:** if a turn-in would push that scrip past 4,000, walks to the Scrip Exchange, buys the item
   (up to the number asked for), and comes back.
4. **At the end** buys as many as the scrips allow, up to the number asked for. Each purchase is checked against
   your bags and scrips. Where the item sits in the shop (page / sub-page) is found once and remembered.

NPC names are settable (defaults "Collectable Appraiser" and "Scrip Exchange"; a nearby NPC with "Appraiser" /
"Scrip" in its name is used if those aren't found). Run logs go to `pluginConfigs/TurnInManager/dumps/`.

IPC (used by Material Stash): `TurnInManager.Scrips.Start(uint item, int count) -> string` ("" = started),
`.IsBusy`, `.Failed`, `.Status`, `.Bought`, `.Stop`.

## Status
0.1: built from community references for these windows (see THIRD_PARTY_NOTICES) and **not yet
tested in game**. Anything unexpected stops the run with a chat message, and window layouts are
written to /xllog. Scrips (0.2): the window callbacks are the ones GatherBuddy Reborn and TheCollector use
(appraiser: job tab 14, item 12, hand in 15; exchange: page 12, sub-page 13, buy 14 + row + amount, then the
confirmation dialog), **not yet tested in game**. Unverified: the hub NPC names, the exchange's first menu (it picks a
row with "Crafter"/"Gatherer"/"Scrip", else the first), and the special-currency numbers for the four scrips.
