# Turn-In Manager

Turn-ins with guardrails. First module: **Grand Company expert delivery**, with seal
spending. Planned modules: Poetics (folding in Poetics Spender) and other currencies.

Commands: `/turnin` (window), `/turnin gc` (run), `/turnin buy` (seal shop only),
`/turnin stop`, `/turnin dump <window>` (writes a game window's data to /xllog, for fixing).

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

## Status
0.1: built from community references for these windows (see THIRD_PARTY_NOTICES) and **not yet
tested in game**. Anything unexpected stops the run with a chat message, and window layouts are
written to /xllog.
