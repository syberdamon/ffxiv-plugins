# Materia Manager

Materia in one place. `/materia` opens it.

## Storage
- **Auto-store** (on by default): when you open your chocobo saddlebag or a retainer's inventory,
  every materia stack in your bags moves there, one stack at a time. Turn it off while you're
  melding (`/materia store off`).
- **Store to:** saddlebag or retainer (`/materia saddlebag` / `/materia retainer`).
- **Saddlebag -> retainer:** with a retainer open it moves saddlebag materia straight over if the
  game allows it; otherwise open the saddlebag and press the button again: it pulls the materia
  into your bags (auto-store to the saddlebag pauses), and the next retainer you open takes it.
- Stacks merge onto existing stacks first; stops if the destination is full.

## Transmutation (Mutamix Bubblypots, Central Thanalan)
- Trades 5 materia for 1 random materia of a different type, at one of the grades handed in,
  sometimes one grade higher.
- Tick which materia may be used and how many of each to **keep**; only materia in your bags is
  used. Batches are five of the same grade by default, mixing types.
- **Go to Mutamix** teleports to Black Brush Station (Lifestream if installed), walks there with
  vnavmesh and talks to him. This first version stops at the transmute window (and saves its
  layout to `pluginConfigs/MateriaManager/dumps`); you hand in the planned batches. Next version
  automates the hand-in from that layout.

## Unverified (needs an in-game check)
- Saddlebag window name (InventoryBuddy) and whether moving saddlebag -> retainer directly works.
- Mutamix's menu wording (looks for "transmut" or "Materia").
