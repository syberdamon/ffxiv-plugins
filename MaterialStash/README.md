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

## Tidy (sorting between retainers)
**Tidy (N)** at the bell (or `/stash tidy`) moves items between retainers; hover it for the plan first.
- An item with a home (pin, name rule, category, group) that sits on a retainer outside its homes goes home.
- An item with no home that's split across retainers is merged onto the one holding the most. Items with no home on a
  single retainer stay where they are. NQ and HQ stay together.
- Retainer to retainer goes through your bags (the game has no direct transfer), so each trip is limited by your free
  bag slots; it takes laps (up to 4, settable) until nothing's left or nothing moves. Anything still in your bags at the
  end is remembered and put away by the next Stash or Tidy.
- After each retainer, the game's own item sort runs on it (`/itemsort ... retainer`; switchable).
- Items the game refuses (a unique item the retainer already has, or one that can't be entrusted) are skipped and listed.
- "Never use" retainers are never touched; groups can opt out ("Tidy may move these": Materia is off by default).
- **Venture returns, after AutoRetainer** (on by default): venture rewards land in your bags. While AutoRetainer
  works, your bags are watched; once it's done, a Stash run takes **only the items that cycle brought back that the
  stock list wants** (directly or as a recipe ingredient). Crystals, items already in your bags, and money-venture
  items (to sell) stay put. Multi mode: this runs as AutoRetainer's character post-processing step (walking to a bell
  if needed, then handing control back). Single character at the bell: it starts when AutoRetainer goes idle with the
  list open.

## Homes: groups, categories, name rules, pins (Categories tab)
Most specific first: **item pin** (one item -> retainers), **name rule** ("name contains Totem" -> retainers),
**game category** (e.g. Rug), **group** (Housing, Armor, Accessories, Weapons & tools, Food & potions, Materials,
Collectibles, Materia, Other: built from the game's own category order; edit freely), then the **overflow order**.
Each group has a **Stash from my bags too** switch: on for Materials (by the material rules), off for the rest, so gear
or housing items you're carrying stay with you unless you turn it on.

## Keep on me
Items never taken out of your bags by Stash, a tidy or the saddlebag buttons (raid food, potions, dark matter...).
Add them on the **Keep on me** tab or with **Keep** on the Bags now tab.

## Venture timer
A small movable window: how many retainers are ready (venture done, or none assigned) and minutes until the next. From
the game's retainer list once you've used a bell this session; before that, from AutoRetainer (its
`PluginState.GetClosestRetainerVentureSecondsRemaining`). Hidden in combat, duties and cutscenes unless you pin it;
left-click it to go to the nearest summoning bell (walks to one in sight with vnavmesh; with none in sight,
Lifestream to the market board first, where cities keep bells close by [Assumption]; also `/stash bell`), right-click
it to open Material Stash. `/stash ventures` toggles it.

## Saddlebag
With the saddlebag open, two buttons sit on top of it: **Mats -> saddlebag** and **Mats -> bags** (stashable materials
only, stacking onto existing stacks first). Also `/stash saddle in` and `/stash saddle out`.

## Stock (refilled through AutoRetainer ventures)
On the **Stock** tab, keep a **minimum** and a **target** of:
- **items**, or
- **recipes**: "Caramel Popcorn, min 150 / target 300" means materials for that many made (the recipe's yield is applied,
  so 300 popcorn = 100 crafts). Crafted ingredients are broken down to their own materials unless you tick
  **stock as is** for one. Several recipes using the same material add up.

"Have" counts bags + saddlebag (as last seen) + every retainer (as last seen), NQ and HQ together, plus what ventures
already out will bring back.

When AutoRetainer is about to send a retainer out, Material Stash picks the most-needed item that retainer can bring
back and hands AutoRetainer that venture (AutoRetainer's own override, `OnSendRetainerToVenture` / `SetVenture`; only
that one send changes, your AutoRetainer plans don't). If nothing is short, or nothing short can come from that
retainer, AutoRetainer does what it would anyway.
- **Order:** items under their minimum first, the furthest under (as a % of the minimum) first. Once every minimum is
  met: **spread evenly** (lowest % of target first) or **closest to its target first** (your choice).
- **Who can go:** any retainer whose class (combat for hunting drops, MIN/BTN/FSH for gathered items and crystals) and
  level fit, with AutoRetainer's recorded gathering / item level, and (gatherers) only for items you've gathered yourself
  (every gathering-log entry the item has, plus the fishing and spearfishing logs). **Fallback:** if a venture we pick
  doesn't take anyway (the retainer comes back to the list on something else), it's noted as refused for that character
  and skipped for a week, so later sends move on to the next stock item, a money venture, or AutoRetainer's own plan.
  Tick **Keep its ventures** for a retainer (Retainers tab) to leave it alone.
- **Crystals** (tick "Venture for crystals too"): a minimum and target per element for each tier (shards, crystals,
  clusters), or per element; counted across your crystal tab and every retainer's combined. Below its minimum a type
  competes with the other short items as usual; above it, crystals come **last of all**, in your tier order, lowest %
  of target first within a tier. Target 0 = no limit (until every crystal tab is full). Crystals your recipes or items
  ask for are added on top of the minimum/target.
- Items no venture brings back (vendor or crafted-only) are listed as "craft/buy/gather yourself".
- Venture amounts are estimated from AutoRetainer's recorded perception / item level (the game's thresholds); unknown
  stats count as the lowest amount.

## Money ventures (Selling tab)
When nothing on the stock list fits a retainer (and it isn't marked "keep its ventures"), it goes for the venture worth
the most on your **home world** instead, if one passes:
- **Value** = items it brings back × the lower of the lowest listing and the median sale (last 7 days), less 5% tax;
  at least the minimum (200 gil by default).
- **Demand**: the item must sell at least N ventures' worth a day (N = 3 by default, adjustable). Counted in items per
  day from the sale history; Universalis's own "velocity" counts sales, not items. Optionally (on by default) what
  other retainers are already fetching and what you hold to sell count against that.
- Nothing passes: AutoRetainer's own plan, as before. Prices: Universalis, NQ, refreshed hourly in the background.
- The "What each retainer would go for now" list shows the choice and the numbers behind it.

What comes back goes on the **to-sell list**: it stays in your bags (Stash leaves it), a sound plays (`<se.6>` by
default), chat lists it with an estimated value, and the stacks are boxed in your inventory (each stack in the
expanded layout; the window and bag/slot numbers in the smaller ones).

**Auto-sell** (off by default): pick a selling retainer; **Sell (N)** at the bell (or `/stash sell`, or at the end of
every Stash run) lists them with the game's own listing call: lowest home-world listing minus your undercut (1 gil),
else the median sale; up to 99 per listing. When the selling retainer's 20 market slots fill up, it moves on to the
next retainer with free slots (no selling retainer picked: any retainer with room, in list order).

## Clean-up (retainer junk audit)
The Clean-up tab goes through everything your retainers hold (as last seen; retainers only, not your bags) and
suggests a verdict for each item. Materials, materia, crystals, stock-list and keep-on-me items are left alone
(switchable). Nothing happens to an item until you approve it.
- **Use**: unlearned minions, cards, orchestrion rolls and so on (the game's own "already learned?" check). The run
  brings them out and uses them.
- **Card: sell for MGP**: Triple Triad cards you already have (trading them in is the next update; for now they
  come to your bags).
- **Keep the look**: gear whose look can't be had any more (no shop, duty, recipe or market source for any item with
  that model) and gear that fits the Armoire. Gear already in your glamour dresser or Armoire is never kept for its look.
- **Exchange**: tokens that buy something (hover to see what).
- **Sell on the market** if a stack fetches at least 1,000 gil after tax (home world: lower of the lowest listing and
  the 7-day median) and more than the vendor pays; else **Grand Company** for green/blue gear (Turn-In Manager takes
  it from there), else **Vendor** ("Have Retainer Sell Items"), else **Desynthesize**, else **Discard**.

**Desynthesize** items are brought to your bags and, after the run (setting on by default, or `/stash desynth`),
desynthesized one at a time the way AutoRetainer does it (the game's Salvage call plus its confirmation, which also ticks "unique/untradable"): the bell is closed first, the result window is closed after
each one, and it stops when your bags are nearly full. Items the game won't desynthesize (desynthesis not unlocked, class
too low) are skipped and listed. The inn trip for looks waits until it's done.

**Spiritbond**: gear with any spiritbond can't go on the market board, so spiritbound copies get no market value
(the run never pulls or lists them for the market). Their value is the vendor price or, if higher, the desynthesis
results [Estimate: the average home-world price of the results players have reported to Teamcraft, one per
desynthesis; that list is partial, so some items show "desynth results unknown"]. Desynthesis also wins for any item
whose results are worth more than selling or vendoring it. Spiritbond is read when a retainer is opened (Scan all once
after updating); records from Allagan Tools don't carry it.

Right-click an item's name to **Try on** gear in the game's Fitting Room (your character, any item), see pictures of
the look (Eorzea Collection glamours, the Console Games Wiki page), open **More information** (Allagan Tools' item window, via its `/moreinfo`), Universalis,
Garland Tools, Teamcraft or the Console Games Wiki, or to copy its name.

Any verdict can be changed per item. Stacks worth more than 10,000 gil get their own look; the rest can be approved a
group at a time. Approving a discard first shows how you'd get each item back (market, vendors, exchanges, crafting,
and the duties it drops in with how hard each is to run today). Items that are easy to get again (solo or a small
group unsynced) are tagged.

**Run clean-up** at the summoning bell takes the approved items out of each retainer into empty bag slots, sells the
vendor ones through that retainer and discards the approved discards (only stacks the run itself took out), then lists
the market ones on your selling retainer first (Selling tab, optional), then on any other retainer with free market slots (20 each). When a retainer is dismissed after vendor sales, the game's
"sold items can't be bought back" prompt is confirmed for you. The glamour dresser is read when you open it and the Armoire
when it's loaded at an inn; with the dresser open, **Take dresser duplicates out** removes extra copies.

**Store looks at an inn** (also automatic after a run, switchable; or `/stash looks`): approved "keep the look" items in
your bags go to an inn room with Lifestream (`/li inn`, skipped if you're already in one), get self-repaired if below
100% condition (Dark Matter and a high enough crafter, as with the game's Repair window; items that won't repair are
listed and stay in your bags), walk to the Armoire with
vnavmesh and store what fits there (free), then the Glamour Dresser for the rest (one Glamour Prism each; without
prisms they stay in your bags). Items the game won't take are listed. No Lifestream: be in an inn room first; no
vnavmesh: stand next to the Armoire / dresser first.

How hard a duty is to run today is a rule of thumb at level 100 [Estimate]: dungeons below the cap, normal trials and
A Realm Reborn to Stormblood extremes are solo unsynced (except the Pool of Tribute (Extreme), which needs a second
player); A Realm Reborn to Stormblood alliance raids take three players (one per alliance); Shadowbringers and later
extremes and savage raids need a full group. Sources: [Sportskeeda: extreme trials you can solo](https://sportskeeda.com/mmo/all-trials-can-fought-solo-final-fantasy-14),
[Consolegameswiki: Syrcus Tower unsynced](https://ffxiv.consolegameswiki.com/wiki/Syrcus_Tower). Duty drops, shop
trades and gil prices come from Teamcraft's data (refreshed by CI).

## Furnishings to the house storeroom
**Furnishings home (N)** at the bell (or `/stash furnish`) takes indoor furnishings off your retainers into empty bag
slots, then goes home with Lifestream (`/li home`), opens `/housing` > **Indoor Furnishings**, and uses each item's
**Store** menu entry to put it in the storeroom. `/stash storeroom` does just the second half (furnishings already in
your bags). Renovation items (walls, flooring, lights, exterior parts), outdoor furnishings and gardening items are
never picked. The storeroom holds as many items as the house can place (cottage 200, house 300, mansion 400, shared
with placed furnishings); what the game won't take is skipped and listed.

Set Lifestream to walk into the house: in Lifestream's house registration, override the teleport behaviour to "Enter
house" (your current setting only teleports to the ward). If the housing menu's "Indoor Furnishings" entry can't be
found to click, it asks you to click it and carries on (and saves a dump so it can be fixed).

Shards, crystals and clusters fill retainers in the bell's list order, each to 9,999 of a type before the next.
**Keep N on me** (global, or per type) leaves that many with you; below it, the run takes the difference back from
the last retainer holding that type.

## Scrip goals (Scrip goals tab, `/stash scrips`)
"I want 20 Mason's Abrasive." A goal is a scrip-exchange item and a target, counted in your bags, saddlebag and
retainers. **Run all goals** (or **Run** on one) repeats until you have the target:
1. **Turn in and buy.** With collectables in your bags (that meet the lowest tier), or scrips enough for one, Turn-In
   Manager goes to your scrip hub, turns them in and buys the item, buying before a turn-in would push scrips past
   4,000.
2. **Craft a batch** of the collectable paying the most scrips per craft that you can make (your level, master
   recipe books, the turn-in quest), or the recipe you pick for the goal. Crafts needed assume the top tier
   [Estimate]; the next turn-in counts what was really paid. A batch is sized so the bag slots you keep free
   (3 by default, since each collectable takes its own slot) are still free afterwards.
3. **Materials, in order:** your bags; then your retainers (walks to the nearest summoning bell and takes whole stacks
   out); then, if on, it stops and lists what's on the market board at or under your price cap (buy them, then
   **Resume**; buying isn't automated yet); then gathering. Vulcan (GatherBuddy Reborn) gathers what's missing and
   makes intermediate items, then crafts. With gathering off, it stops when gatherable materials run out.
4. **Crafting plugin:** Vulcan (`/vulcan craft <recipe> <count>`) or Artisan (its IPC). Artisan only crafts the
   collectable itself, so a batch that needs gathering or intermediate crafts goes to Vulcan.

It stops a goal (and goes on to the next) when there's no usable recipe, a material can't be gathered (mob drops,
vendor items: buy them or stock them), the bags have no room for a batch, a batch made nothing, or a turn-in changed
nothing. **Stock mats** on a goal puts its collectable's recipe on the Stock list, so ventures bring back its
materials.

**Gatherer scrips** (e.g. items bought with purple/orange gatherers' scrips): instead of crafting, a batch is gathered
with GatherBuddy Reborn's auto-gather. Auto picks the Miner/Botanist collectable paying the most you can turn in
(or pick one per goal; timed nodes are slow, so pick a regular one if Auto chose a timed one). For each batch:
your switched-on auto-gather lists are switched off (and noted), a list named "Material Stash: scrip goals" is
created or reused with the collectable and the amount, it's switched on, auto-gather starts, and it's switched off
when the batch is in your bags (or bags are down to the free slots you keep). Then your lists are switched back on.
If a run is cut off, the tab shows the lists still off and a button to switch them back. Fishing collectables
aren't automated (bait). GatherBuddy has no IPC for lists, so this reaches its list manager through reflection
[Assumption: its internal names as of Nov 2026]; after a GatherBuddy update that renames them, the run stops with
a message and changes nothing.

Needs Turn-In Manager (turn-ins, buying), and GatherBuddy Reborn and/or Artisan (crafter scrips) or GatherBuddy
Reborn (gatherer scrips). Vulcan has no stop command: Stop
ends our run, but a Vulcan batch already going has to be stopped in Vulcan's window.

## Retainer records
The game only shares a retainer's inventory while it's open, so records come from: the one-time scan (or Allagan
Tools), then every time a retainer's inventory is open, by you, AutoRetainer or a run. The Retainers tab shows when
each was last seen. A retainer never seen is assumed to have room; it's checked when the run opens it.

## Unverified (needs an in-game check)
- Closing the retainer inventory with the window's close (selecting a retainer from the list is confirmed in game).
- Auto-sell: the "Sell items in your inventory on the market" menu row and the game's direct listing call
  (`MoveToRetainerMarket`, as Restocker uses it).
- The boxes around inventory slots (slot order read from the expanded inventory's grids).
- The item sort commands for a retainer (`/itemsort clear|condition|execute retainer`).
- Moving part of a crystal stack: the item menu's "Entrust..." / "Retrieve..." then the number prompt. Whole stacks
  move directly. A wrong menu guess skips that crystal and saves a dump in `pluginConfigs/MaterialStash/dumps/`.
- Self-repairing gear that's in your bags (not equipped / Armoury Chest) before storing looks: the game's repair call
  is used directly; if it won't take bag items, they're listed as "needs repair" instead of stored.
- Allagan Tools' `GetCharacterItems` layout ([item, flags, owner, quantity] per stack).
- Reading AutoRetainer's recorded retainer stats (`GetAdditionalRetainerData`) into our own type; if it fails, stats
  count as unknown.
- Whether AutoRetainer's venture override also applies in multi-character mode (confirmed for its normal bell run).
- The "gathered it yourself" check uses a game-function signature (from AutoRetainer); after a patch it may be missing,
  and then every gathering venture is assumed allowed (the refused-venture fallback still catches it after one try).
- Storeroom: the housing menu's layout (finding "Indoor Furnishings"), layout mode detected by the Housing agent, the
  item menu's "Store" entry while Indoor Furnishings is open, and which categories count as indoor furnishings.
- Clean-up: "Have Retainer Sell Items" from the bag item menu with the retainer's inventory open; discarding with
  the game's discard call (confirmed with its yes/no); using items with the game's item action; taking items out of the
  dresser; reading the Armoire (`IsItemInCabinet`).
- Storing looks: the inn-room check (territory "intended use" 2), opening the Armoire / dresser by interacting with
  them, the Armoire's store call (`StoreCabinetItem`), and the dresser's store event (Glamour Creation list event 14,
  then its confirmation, as Spring Cleaning does it).

- Scrip goals: how long Vulcan's gather-then-craft pipeline looks idle between steps (a batch counts as over after 30
  seconds with no crafting, gathering, travel or new collectables); whether Vulcan's solver reaches the top tier.
- Gatherer-scrip goals: reaching GatherBuddy Reborn's list manager by reflection (Dalamud's plugin manager ->
  GatherBuddyReborn instance -> AutoGatherListsManager), and whether auto-gather gathers the collectable version
  with its collectable rotation (expected: yes).
- Crystals aren't taken out of retainers by scrip goals (keep enough on you with the crystal "keep" setting).

Confirmed from AutoRetainer's source: `AutoRetainer.PluginState.IsBusy`, `OnSendRetainerToVenture`, `SetVenture`.
