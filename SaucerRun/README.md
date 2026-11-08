# Saucer Run

One floating button for the Gold Saucer chores. `/saucer` opens the settings; `/saucer run` and `/saucer stop`.

## The button
Shows **Mini Cactpot n/3** (today) and **Jumbo Cactpot n/3** (this week), amber until done, green after; then it just
offers cards ("done · 12 cards"). Hover for details (resets, winnings checked, cards in your bags). Click to run or
stop; right-click for settings. Hidden in combat and duties unless you pin it.

## A run
1. **Learn cards**: any Triple Triad card in your bags you don't have yet is used (learned).
2. **Teleport** to the Gold Saucer (if you're not there).
3. **Jumbo Cactpot** (if this week isn't done): the **cashier** first (winnings from the last drawing), then the
   **broker** for this week's three tickets.
4. **Mini Cactpot** (if today isn't done): the broker, once per ticket.
5. **Sell cards**: the Triple Triad Trader buys every card in your bags you've already learned, for MGP. A card you
   haven't learned is never sold.

The Cactpot games themselves are played by **Saucy** (its Mini Cactpot and Jumbo Cactpot modules must be on; your
number choice is Saucy's setting). Saucer Run walks there (**vnavmesh**), targets and talks to the NPC (Saucy only takes over
a conversation with the NPC you have targeted), starts the ticket purchase itself if nothing has within a second and a
half ("Purchase a ... ticket", then yes to the price), waits until Saucy is done, and moves on. Without Saucy, the Cactpots are skipped.

## Counting
Like DailyDuty: each time the Mini Cactpot board opens is a ticket played, each time the Jumbo number window opens is
a ticket bought, the cashier's reward list means winnings checked. Mini resets daily at 15:00 UTC; Jumbo at your
region's drawing (NA Sunday 02:00 UTC, EU Saturday 19:00 UTC, JP Saturday 12:00 UTC, OCE Saturday 09:00 UTC). If a
broker opens nothing when talked to, that game counts as done. Played today before installing? Fix the counts in
the settings.

## Unverified (needs an in-game check)
- NPC spots before they're in sight: converted from Teamcraft's map positions (the trader's from AutoDuty); heights
  are found on the navmesh. [Estimate]
- The Triple Triad Trader: menu entry with "Sell"/"Card" (else the second), the exchange list's layout (count at
  value 124 + row, card id at 164 + row, as ECommons reads it), the pick callback (0, row) and the amount dialog
  (0, count), as AutoDuty does it.
- Teleporting with the game's own teleport call; the Gold Saucer aetheryte found from game data.
- Whether Saucy buys all three Jumbo tickets in one conversation (if not, the broker is talked to again).
