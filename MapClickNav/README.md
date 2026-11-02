# Map Click Nav

Hold **Ctrl** (configurable) and **left-click** a spot on your current zone's map.
The plugin:
1. Drops your flag at that spot.
2. Summons **Mount Roulette** if you're not mounted (optional, on by default).
3. Has **vnavmesh** fly you there if flight is unlocked in the zone, or ride/walk there if not.

A **Stop** button appears while a trip is running. Also: `/mapnav stop`.

Commands: `/mapnav` (settings), `/mapnav stop`, `/mapnav on`, `/mapnav off`.

**Requires vnavmesh** (installed and enabled). The first trip in a zone may wait a
few seconds while vnavmesh builds that zone's mesh.

## Limits
- Only works on the map of the zone you're standing in (no cross-zone travel).
- The click also reaches the game, so clicking directly on a map icon may trigger it.
- Accuracy is about 2 yalms (the map's cursor readout has one decimal).
- Mount Roulette fails in combat, indoors, or where mounts aren't allowed; after
  the timeout (default 6 s) it travels on foot.

## Unverified (needs an in-game check)
- Mount Roulette is pressed as GeneralAction row 9 (from memory).
- Map-to-world conversion is the inverse of Dalamud's `MapUtil` formula; if trips
  land consistently offset from where you click, report it.
