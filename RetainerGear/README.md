# Retainer Gear

Tracks your retainers' equipped gear and lists the upgrades that would put each one at the
highest item level available **without raid or savage gear**: crafted gear, dungeon drops, and
gear bought with uncapped currencies (Allagan Tomestones of Mathematics and Poetics by default).
Combat and gatherer retainers both. `/rgear` opens it.

## How it works
- **Roster:** name, job and level of every retainer, read once you've used a summoning bell.
- **Gear:** the game only shares a retainer's equipment while that retainer is open, so open each
  one once (AutoRetainer opening them counts). After you equip upgrades, the next time the
  retainer is open the list updates.
- **Best available:** for each slot, the highest item level piece the retainer's job can wear at
  their level from the enabled sources. Rings: two copies unless the ring is unique.
- **You have it:** spare copies in your bags, armoury, saddlebag or any retainer's inventory are
  matched to a retainer that needs them ("Hand these over"), so you don't buy what you own.
- **Shopping list:** everything still needed, with quantity, which retainers, where it comes
  from, and the lowest market board price on your data center (Universalis) for crafted pieces.

## Sources
- Crafted: game data (recipes).
- Dungeon drops and shop trades: Teamcraft's data (MIT, (c) Flavien Normand), bundled and
  refreshed by `.github/scripts/sync_gear_sources.py`. Brand-new dungeons are often missing
  there; gear you loot inside a dungeon is remembered as dropping there.
- Race/sex-locked gear is skipped (a retainer's race isn't known).

## Unverified (needs an in-game check)
- Retainer equipment slot order (main hand 0 ... rings 11/12) and the retainer name offset.
- Teamcraft's shop data for the newest tomestone gear.
