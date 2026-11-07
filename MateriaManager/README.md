# Materia Manager

Materia in one place. `/materia` opens it.

## Storage
The intended loop: materia you pick up goes to the **saddlebag**; when you visit your **materia
retainer**, it's topped up to your targets; what's left over sits in your **bags** for transmuting.

- **Auto-store to the saddlebag** (on by default): opening the saddlebag moves materia from your
  bags into it. Turn off while melding (`/materia store off`).
- **Pull all from the saddlebag** (`/materia pull`): everything comes into your bags and is
  *held* there: auto-store won't put it back until you **Release** it (or your bags run out).
- **Pull by grade** (`/materia pull 11-12`, `/materia pull 1-6`; one number = that grade and up):
  materia of those grades comes into your bags from the saddlebag and/or the open retainer
  (tick which). The top grades for melding, the low ones for clearing out. Held in your bags
  afterwards, like "pull all".
- **Materia retainer:** pick which retainer holds your materia, and a minimum and maximum of each
  (default 800 / 800; optionally only from a grade up; per-materia overrides in the table).
  Opening that retainer's inventory balances it: anything below the minimum is topped up to the
  maximum from your bags (then the saddlebag, if it's open), and anything above the maximum comes
  back to your bags. Exact amounts: stacks are split as needed. What stays in your bags is held
  for transmuting.
- One step every 0.4 s; stops if a window closes or there's no room.

## Transmutation (Mutamix Bubblypots, Central Thanalan)
- Trades 5 materia for 1 random materia of a different type, at one of the grades handed in,
  sometimes one grade higher.
- Tick which materia may be used and how many of each to **keep**; only materia in your bags is
  used. Batches are five of the same grade by default, mixing types.
- **Go to Mutamix** teleports to Black Brush Station (Lifestream if installed), walks there with
  vnavmesh and talks to him. In his transmute window (the game's multi-item hand-in window) it adds
  each batch through the items' right-click menus, confirms, and repeats, lowest grade first, up
  to a per-trip cap (default 10). It reports each result, and upgrades.
- By default it won't use what your materia retainer still needs to reach its maximum (as last
  seen), so pull, transmute and balance don't fight each other.

## Unverified (needs an in-game check)
- Saddlebag window name (InventoryBuddy) and the stack split (InventoryManager.SplitItem) puts the new stack in a free slot of the same bags.
- Mutamix's menu wording (looks for "transmut" or "Materia"); the item-menu option that adds
  materia to the transmute window; the window's Confirm button.

## Purge
Sells low-grade materia you have fewer than five of (not enough to transmute): below a grade you
pick (default below VII), fewer than 5 owned across bags, saddlebag and materia retainer. Only
sells from your bags, and never materia with a Keep or a retainer target. Optionally keeps them if
their grade still has five in total (transmute batches can mix types).
- **Go to the nearest vendor and sell** (`/materia purge vendor`): walks to the closest merchant
  in sight, opens their shop, and sells each stack through its right-click "Sell".
- **Sell here** (`/materia purge`): with a shop open, or a retainer open ("Have Retainer Sell Items").

Transmute batches run lowest grade first, so an upgraded result can complete a batch of the next
grade; "Use all" / "Use none" / "Set Keep for all" fill the table in one click.
