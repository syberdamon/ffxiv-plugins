# Map Click Nav

Hold **Ctrl** (configurable) and **left-click** a spot on **any zone's map**.
The plugin:
1. Drops your flag at that spot.
2. **Teleports** to the nearest attuned aetheryte when that's faster:
   - another zone: always teleports there first;
   - your zone: teleports if the aetheryte is at least 300 yalms closer to the
     target than you are (same rule CBT uses; adjustable).
3. Waits for the zone to load and for vnavmesh to build its mesh.
4. Mounts (Mount Roulette) if you're not mounted.
5. Has **vnavmesh** fly you there if flight is unlocked in the zone, or ride there if not.

A **Stop** button appears while a trip is running. Also: `/mapnav stop`.

Commands: `/mapnav` (settings), `/mapnav stop`, `/mapnav on`, `/mapnav off`.

**Requires vnavmesh** (installed and enabled).

## Limits
- Teleports cost gil as usual, and only go to aetherytes you've attuned to.
- No aethernet hops: in big cities it teleports to the main aetheryte and
  travels from there.
- The click also reaches the game, so clicking directly on a map icon may trigger it.
- Accuracy is about 2 yalms (the map's cursor readout has one decimal).
- Mounting fails in combat, indoors, or where mounts aren't allowed; after the
  timeout (default 6 s) it travels on foot.

## Unverified (needs an in-game check)
- Mount action is GeneralAction 24 (what CBT uses). It's one of Mount Roulette /
  Flying Mount Roulette.
- Map-to-world conversion is the inverse of Dalamud's `MapUtil` formula; if trips
  land consistently offset from where you click, report it.

Teleport flow and aetheryte lookup adapted from CBT by Puni.sh (BSD-3-Clause);
see [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md).
