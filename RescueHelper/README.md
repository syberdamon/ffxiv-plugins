# Rescue Helper

For healers. A small party panel shows who's standing in an AoE that's about to go off, with a
countdown. **Click their row to Rescue them**, or switch to **automatic**. `/rescue` for settings,
`/rescue on|off|click|auto|panel`.

## How it decides
- **Danger zones come from BossMod / BossMod Reborn** (must be running, with a module for the
  fight): each party member's spot is checked against every zone BossMod knows, and the panel
  shows when the next one goes off. Default: flag anyone in a zone resolving within 3 s.
- **Rescues only when:** your own spot is safe, Rescue is ready, and they're within range.
- **Never pulls:** a tank holding enemies, or a player who got a head marker in the last 8 s
  (spreads and stacks make their spot "unsafe" on purpose). Dead players are ignored.
- **Automatic:** waits until they've been in danger 0.5 s (a chance to walk out) and the AoE
  resolves within 1.8 s, then Rescues the most urgent one. All adjustable.

## Limits
- BossMod's zones are worked out from your point of view, and it reports one "next" time for
  all zones, so countdowns are approximate.
- Fights without a BossMod module show nothing.

## Unverified (needs an in-game check)
- Rescue's range (30 yalms assumed) and the head-marker packet category (0x22, as in Mit Planner).
