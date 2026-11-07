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

**Healers** (WHM, SCH, AST, SGE) start with a **Tank** group: clicking casts on the tank
who's tanking (the one enemies are attacking), no targeting needed.

| | Tank | Defensive (group) |
|---|---|---|
| WHM | Divine Benison, Aquaveil, Tetragrammaton, Benediction, Regen | Temperance, Plenary Indulgence, Asylum, Liturgy of the Bell |
| SCH | Protraction, Excogitation, Lustrate, Aetherpact | Sacred Soil, Expedient, Fey Illumination, Indomitability, Whispering Dawn, Seraph, Seraphism |
| AST | Essential Dignity, Celestial Intersection, Exaltation, Synastry, Aspected Benefic | Neutral Sect, Collective Unconscious, Earthly Star, Horoscope, Macrocosmos, Celestial Opposition |
| SGE | Kardia, Taurochole, Druochole, Haima, Krasis | Kerachole, Holos, Panhaima, Physis II, Ixochole, Philosophia |

Support: job buffs plus Swiftcast, Lucid Dreaming, Surecast, Rescue, Sprint, Limit Break.
Regen and Aspected Benefic are GCD spells, on the grid only as upkeep reminders.

## Alerts (in combat; Display tab)
Icons pulse gold when they're due:
- **Tank group**, a few seconds (default 5) before a tank buster.
- **Defensive (group)**, before a raidwide or stack (tanks too: Reprisal, Divine Veil...).
- **Regen / Aspected Benefic / Kardia** while yours isn't on the tank.
Buster/raidwide timing comes from Mit Planner's timeline (0.9.7+), or BossMod's when Mit
Planner has none for the fight.

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
