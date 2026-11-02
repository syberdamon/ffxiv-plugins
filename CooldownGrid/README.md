# Cooldown Grid

Pick any of your job's cooldown abilities and sort them into groups. Each group
is its own window: a grid of ability icons showing seconds remaining and charges.
**Click an icon to use the ability**, the same as pressing it on a hotbar.

Commands: `/cdgrid` (settings), `/cdgrid lock`, `/cdgrid unlock`, `/cdgrid show`, `/cdgrid hide`.

## Setup
1. `/cdgrid` > **Abilities**: every off-GCD ability with a cooldown that your
   current job can use at your level. Set each one's group, or leave it Hidden.
   Use the arrows to reorder within a group.
2. **Groups**: three defaults (Defensive (self), Defensive (group), Support) plus
   any you add. Each window has its own columns, icon size, lock, title bar and
   background settings.
3. Settings are saved **per job**. Warrior (and Marauder) start with a layout:
   - Defensive (self): Rampart, Vengeance/Damnation, Thrill of Battle,
     Raw Intuition/Bloodwhetting, Equilibrium, Holmgang
   - Defensive (group): Shake It Off, Reprisal, Nascent Flash
   - Support: Provoke, Shirk, Interject, Low Blow, Arm's Length

## How the icons read
- Dark with a number: on cooldown (for charge abilities: out of charges; the
  number is time to the next charge).
- Faint red: off cooldown but the game would reject it right now (no target, out
  of range, wrong state). Can be turned off under Display.
- Yellow number bottom-right: current charges.

## Notes
- The ability list comes from game data, so every combat job works; only Warrior
  has a starter layout so far.
- Targeting works like a hotbar: self abilities hit you; targeted ones (Nascent
  Flash, Provoke, Shirk) use your current target.
- Not yet included: items (potions) and other non-action buttons.

## Unverified (needs an in-game check)
- Starter-layout action IDs are from memory. A wrong ID just won't show up; tell
  me which ability is missing.
- Clicking should behave exactly like a hotbar press, including targeting.
