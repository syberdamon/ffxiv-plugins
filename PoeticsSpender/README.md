# Poetics Spender

Turns all your Allagan Tomestones of Poetics into Grade 3 topsoil in one click,
and pops up an alert when you're close to the Poetics cap.

Commands: `/poetics` (settings), `/poetics run`, `/poetics trade` (trade Ore/Shell you
already hold to Bertana), `/poetics stop`,
`/poetics dump <window>` (writes a game window's data to /xllog, for fixing).

Requires **vnavmesh**. English client (vendor and menu names are matched in English).

## The route (both NPCs stand together in Idyllshire)
1. **Hismena** (5.7, 5.2) sells Unidentifiable Ore / Shell for **150 Poetics** each
   (menu "Allagan Tomestones of Poetics (Special Arms)"; needs the quest "A Great New Nation").
2. **Bertana** (5.9, 5.2), "Uncanny Knickknacks": 1 Ore -> **Grade 3 Thanalan Topsoil**,
   1 Shell -> **Grade 3 Shroud Topsoil**.

A full 2,000 Poetics buys 13 topsoil (50 Poetics left over). No Poetics route to
Grade 3 La Noscea Topsoil was found, so it isn't offered.

## What a run does
1. Checks you're out of combat and duties, have at least 150 Poetics, 2 free bag
   slots, and vnavmesh loaded.
2. Teleports to Idyllshire (skipped if you're already there).
3. Walks to Hismena, opens her Poetics menu, buys as many tokens as your Poetics allow.
4. Walks to Bertana and trades the tokens bought this run for topsoil.
5. Stays in Idyllshire. A status window with a **Stop** button shows while it runs.

## Safety
- **First-time confirmation:** shop rows are found by item name. The first time a
  row is used, the status window shows what it found and waits for **Buy**.
  Confirmed rows are remembered (Settings > "Forget" to reset).
- A Yes/No that doesn't name the item is answered **No** and the run stops.
- Every purchase is checked against your inventory count.
- Anything unexpected stops the run with a chat message; nothing is retried blindly.

## Alert
- Pops up at **1,800** Poetics (configurable), outside combat and duties.
- Click **Buy N x topsoil** to run. **x** hides it until you've spent your Poetics.
- **Position:** Settings > "Place the alert" shows it so you can drag it; or type X/Y.
  Size is adjustable.

## Known limits / first-test notes
- [Assumption] The shop and menu click layouts follow common community automation;
  they haven't been tested against these two NPCs yet. If a step fails, the chat
  message and `/xllog` (`[dump]` lines) say exactly where.
- If an NPC opens with dialogue instead of a menu, or the game asks for a quantity
  in a separate window, the run stops (not handled yet).
