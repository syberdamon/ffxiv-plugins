# Tank Stance Manager

**Control panel** (`/tsm` shows/hides it): ON/OFF button, MT/OT buttons, an
Options button, and a one-line status. It remembers whether it was open and can
be locked in place (Options > Control panel).

Commands: `/tsm` (panel), `/tsm options`, `/tsm on`, `/tsm off`, `/tsm mt`, `/tsm ot`.
Ships disabled (OFF).

**MT mode:** tank stance on at all times, in or out of combat. Re-applies after
death or a job swap.

**OT mode** (combat only; by default only in duties with another tank present):
- Stance ON when your enmity drops below the low mark (default 75% of the MT).
- Stance OFF when it reaches the high mark (default 90%).
- Stance ON if you are pushed to #3, even inside the band (checkbox, default on).
- Stance OFF immediately if you ever become #1.

**Loose mobs** (trash pulls, both off by default): a mob attacking a party member who
isn't a tank (pulled off you, or missed by your AoEs) for 1 s or more.
- *Target loose mobs*: switches your target to the worst one (on the lowest-HP player).
- *Provoke loose mobs*: Provokes it when Provoke is ready and in range.
- Stands by while a boss is up and leaves mobs on the other tank alone (both adjustable).
- `/tsm provoke` and `/tsm target` toggle them.

Jobs: PLD/GLA, WAR/MRD, DRK, GNB.

## Things to know
- Enmity is read from your **current target's** enmity list. Keep the boss targeted.
- Stance action/status IDs in `StanceController.cs` were written from memory.
  Verify with the "Stance detected" readout in `/tsm` while toggling stance by
  hand on each job.
- At most one stance press per 2.5 s (adjustable); never while casting or
  animation-locked.
