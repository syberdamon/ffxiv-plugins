# Cooldown Grid

Pick any of your job's cooldown abilities and sort them into groups. Each group
is its own window: a grid of ability icons showing seconds remaining and charges.
**Click an icon to use the ability**, the same as pressing it on a hotbar.

Commands: `/cdgrid` (settings), `/cdgrid lock`, `/cdgrid unlock`, `/cdgrid show`, `/cdgrid hide`.

## Setup
1. `/cdgrid` > **Abilities**: every off-GCD ability with a cooldown that your
   current job can use at your level. Set each one's group, or leave it Hidden.
   Use the arrows to reorder within a group.
2. **Groups**: four defaults (Defensive (self), Defensive (group), Support,
   **OT Sent Mits**) plus any you add. Icons in OT Sent Mits cast on the other
   tank in your party without targeting them (any group can do this: "Cast on
   the other tank"). Each window has its own columns, icon size, lock, title bar and
   background settings.
3. **Send** column: abilities the game lets you cast on a party member get a
   Send box. Ticking it also shows the ability in **OT Sent Mits**, where
   clicking casts it on the other tank. Self-only abilities (Guardian, Rampart,
   ...) have no Send box.
4. Settings are saved **per job**. Every tank starts with a layout (use
   "Reset this job to the starter layout" to apply it to a job you've already set up):

| | Defensive (self) | Defensive (group) | OT Sent Mits |
|---|---|---|---|
| PLD | Rampart, Sentinel/Guardian, Bulwark, (Holy) Sheltron, Hallowed Ground | Reprisal, Divine Veil, Passage of Arms | Intervention, Shirk |
| WAR | Rampart, Vengeance/Damnation, Thrill of Battle, Raw Intuition/Bloodwhetting, Equilibrium, Holmgang | Reprisal, Shake It Off | Nascent Flash, Shirk |
| DRK | Rampart, Shadow Wall/Shadowed Vigil, Oblation, Dark Mind, The Blackest Night, Living Dead | Reprisal, Dark Missionary | The Blackest Night, Shirk |
| GNB | Rampart, Nebula/Great Nebula, Camouflage, Heart of Stone/Corundum, Aurora, Superbolide | Reprisal, Heart of Light | Heart of Corundum, Shirk |

   Support for tanks: Provoke, Interject, Low Blow, Arm's Length, Sprint,
   Limit Break. Shirk lives in OT Sent Mits: it always goes to the other tank,
   whichever group you put it in (0.4.1 moves it there in existing layouts). Every other class starts with Sprint + Limit Break in Support.

## How the icons read
- Dark with a number: on cooldown (for charge abilities: out of charges; the
  number is time to the next charge).
- Faint red: off cooldown but the game would reject it right now (no target, out
  of range, wrong state). Can be turned off under Display.
- Yellow number bottom-right: current charges (for Limit Break: filled bars).

## Notes
- The ability list comes from game data, so every combat job works; the four
  tanks have starter layouts so far.
- Targeting works like a hotbar: self abilities hit you; targeted ones
  (Provoke, Interject...) use your current target. Exceptions: anything in OT
  Sent Mits, and Shirk anywhere, go to the other tank in the party.
- Not yet included: items (potions) and other non-action buttons.

## Unverified (needs an in-game check)
- Starter-layout action IDs are from memory. A wrong ID just won't show up; tell
  me which ability is missing.
- Clicking should behave exactly like a hotbar press, including targeting.
