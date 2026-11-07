# Material Stash

Common crafting materials onto your retainers, in the order you choose. `/stash` opens the settings.

## Use
1. Open a summoning bell (after AutoRetainer has finished, if you run it). A bar sits above the retainer list, on the right.
2. First time: **Scan all** opens every retainer once and records what it holds (nothing moves). Or, on the
   Retainers tab, **Fill in unseen retainers from Allagan Tools**.
3. Set the **overflow order** (Retainers tab) and, if you like, **category homes** (Categories tab).
4. **Stash mats** visits each retainer something goes to, deposits, and goes back to the list. Ventures,
   sales and everything else are left alone. **Stop** (or `/stash stop`) at any time.

Nothing runs by itself. It won't start, and stops at once, if AutoRetainer is busy.

## What counts
- **Gathered:** from a mining/botany node, fishing or spearfishing, and used in a recipe.
- **Other recipe materials:** used in a recipe, not made by one, tradeable, common (white) rarity, not gear:
  hides, bones, fleece, reagents... Boss and glamour-weapon mats are almost always untradeable or green/blue, so
  they're left out. Vendor-bought recipe materials also pass; exclude any you'd rather keep.
- **Never:** collectables, gear, crafted items (ingots, lumber, thread...).
- **Include / exclude** anything by hand on the **Bags now** tab, which shows every stack and where it would go.

## Where things go
For each stack, the first retainer with room from:
1. retainers that already hold that item (keep together; switchable),
2. the item category's home retainers, in your order,
3. the overflow order.

"Room" is a stack of the same item (same quality) with space, or a free slot; you can keep N slots free on
every retainer. Retainers marked **never use** (e.g. your materia retainer) are skipped. A second pass picks up
anything a full retainer bounced back to one already visited.

## Crystals
Shards, crystals and clusters fill retainers in the bell's list order, each to 9,999 of a type before the next.
**Keep N on me** (global, or per type) leaves that many with you; below it, the run takes the difference back from
the last retainer holding that type.

## Retainer records
The game only shares a retainer's inventory while it's open, so records come from: the one-time scan (or Allagan
Tools), then every time a retainer's inventory is open, by you, AutoRetainer or a run. The Retainers tab shows when
each was last seen. A retainer never seen is assumed to have room; it's checked when the run opens it.

## Unverified (needs an in-game check)
- Selecting a retainer from the list (callback 2, list index), and closing the inventory with the window's close.
- Moving part of a crystal stack: the item menu's "Entrust..." / "Retrieve..." then the number prompt. Whole stacks
  move directly. A wrong menu guess skips that crystal and saves a dump in `pluginConfigs/MaterialStash/dumps/`.
- Allagan Tools' `GetCharacterItems` layout ([item, flags, owner, quantity] per stack).
- AutoRetainer's `AutoRetainer.PluginState.IsBusy`.
