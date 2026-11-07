# Raise Callouts

Posts "Raising <name>" in party chat whenever **you** raise someone: Raise, Resurrection,
Ascend, Egeiro, Verraise, Angel Whisper. It watches the cast itself, so it works no matter what
pressed the button (RotationSolver Reborn, Wrath, a macro, your hotbar). `/raisecall` for settings,
`/raisecall on|off|test`.

- **Hard casts:** posted 0.3 s into the cast bar (adjustable), so an instant cancel says nothing.
- **Instant raises** (Swiftcast, Dualcast): no cast bar, so posted when the raise lands.
- **Once per raise;** nothing if someone else's raise is already on them.
- **Message template:** `{target}`, `{spell}`, `{how}` (" (instant)"). Default `Raising {target}{how}`.
- **Party chat or echo** (only you see it). Only in a party by default; optional duties-only.

## Unverified (needs an in-game check)
- Status 148 = "Raise" (resurrection pending), used to skip targets someone else is raising.
