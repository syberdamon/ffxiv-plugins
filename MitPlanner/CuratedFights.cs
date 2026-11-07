using System.Collections.Generic;

namespace MitPlanner;

/// <summary>Hand-checked tags and head markers for a fight. Applied on top of the cactbot-derived defaults.</summary>
public sealed record CuratedFight(
    IReadOnlyDictionary<string, MechanicType> Tags,
    IReadOnlyDictionary<string, uint>? HeadMarkers = null);

/// <summary>
/// The curated layer. cactbot's triggers only tag what they call out (tank busters, raid-wides,
/// stacks...), so fights here fill gaps and fix mistakes. Add a fight when its defaults are wrong.
/// Order of precedence: your in-game edits > this list > cactbot triggers.
/// </summary>
public static class CuratedFights
{
    public static readonly IReadOnlyDictionary<uint, CuratedFight> ByTerritory = new Dictionary<uint, CuratedFight>
    {
        // The Cinder Drift (Ruby Weapon, normal)
        [897] = new(new Dictionary<string, MechanicType>
        {
            ["Stamp"] = MechanicType.TankBuster,
            ["Ruby Claw"] = MechanicType.TankBuster,
            ["Optimized Ultima"] = MechanicType.Raidwide,
            ["Optimized Ultima Enrage"] = MechanicType.Raidwide,
            ["Outrage"] = MechanicType.Raidwide,
            ["High-Powered Homing Lasers"] = MechanicType.Stack,
            ["Ruby Dynamics"] = MechanicType.Mechanic,
            ["Ruby Ray"] = MechanicType.Mechanic,
            ["Homing Lasers"] = MechanicType.Mechanic,
            ["Liquefaction?/Undermine?"] = MechanicType.Mechanic,
            ["Undermine?/Liquifaction?"] = MechanicType.Mechanic,
            ["Ravensflight"] = MechanicType.Mechanic,
            ["Meteor Stream"] = MechanicType.Mechanic,
            ["Magitek Meteor"] = MechanicType.Mechanic,
            ["Bradamante"] = MechanicType.Mechanic,
        }),

        // Castrum Marinum (Emerald Weapon, normal)
        // Tank busters: Emerald Shot (cast on the tank), Divide Et Impera (head marker 00DA on the tank).
        [934] = new(new Dictionary<string, MechanicType>
        {
            ["Emerald Shot"] = MechanicType.TankBuster,
            ["Divide Et Impera"] = MechanicType.TankBuster,
            ["Optimized Ultima"] = MechanicType.Raidwide,
            ["Magitek Magnetism"] = MechanicType.Mechanic,
            ["Emerald Crusher"] = MechanicType.Mechanic,
            ["Split"] = MechanicType.Mechanic,
            ["Sidescathe"] = MechanicType.Mechanic,
            ["Primus Terminus Est"] = MechanicType.Mechanic,
            ["Secundus Terminus Est"] = MechanicType.Mechanic,
            ["Tertius Terminus Est"] = MechanicType.Mechanic,
            ["Legio Phantasmatis"] = MechanicType.Mechanic,
        },
        new Dictionary<string, uint>
        {
            ["Divide Et Impera"] = 0x00DA,
        }),

        // The Cloud Deck (Diamond Weapon, normal). Auri Doomstead is cast on the tank (x2 in phase 2).
        [950] = new(new Dictionary<string, MechanicType>
        {
            ["Auri Doomstead"] = MechanicType.TankBuster,
            ["Diamond Rain"] = MechanicType.Raidwide,
            ["Outrage"] = MechanicType.Raidwide,
            ["Diamond Flash"] = MechanicType.Stack,
            ["Homing Laser"] = MechanicType.Mechanic,
            ["Auri Cyclone 1"] = MechanicType.Mechanic,
            ["Vertical Cleave"] = MechanicType.Mechanic,
            ["Claw Swipe"] = MechanicType.Mechanic,
            ["Adamant Purge"] = MechanicType.Mechanic,
            ["Photon Burst"] = MechanicType.Mechanic,
            ["Diamond Shrapnel"] = MechanicType.Mechanic,
        }),

        // ---- From Combat Recorder logs, 2026-11-04 (one run each; review with more runs).
        // Each: what cactbot left untagged or mis-tagged, and what the log showed it doing.

        // The Lunar Subterrane: Antipodal Assault hit all 4 for ~35% (line stack); Void Dark II spreads
        // hit ~3.7 of 4 for ~28% at once (party mit helps; cactbot: mechanic).
        [1164] = new(new Dictionary<string, MechanicType>
        {
            ["Antipodal Assault"] = MechanicType.Stack,
            ["Void Dark II (spread)"] = MechanicType.Raidwide,
        }),

        // Alexandria: Amalgamight cast on the tank for ~29%; Light of Salvation hit all 4 for ~37%;
        // Quarantine (3 of 4, ~40%) and Overexposure (all 4, ~17%) are stacks.
        [1199] = new(new Dictionary<string, MechanicType>
        {
            ["Amalgamight"] = MechanicType.TankBuster,
            ["Light of Salvation"] = MechanicType.Raidwide,
            ["Quarantine"] = MechanicType.Stack,
            ["Overexposure"] = MechanicType.Stack,
        }),

        // Alexander - The Burden of the Son (A8N): J Kick all 8 for ~34%, Mind Blast all 8 for ~16%,
        // Long Needle a stack (6 of 8, ~27%).
        [523] = new(new Dictionary<string, MechanicType>
        {
            ["J Kick"] = MechanicType.Raidwide,
            ["Mind Blast"] = MechanicType.Raidwide,
            ["Long Needle"] = MechanicType.Stack,
        }),

        // AAC Heavyweight M2 (normal): Hot Impact hit both tanks (11-19%); Deep Impact cast on one tank;
        // Pyrotation hit all 8 for ~16%.
        [1322] = new(new Dictionary<string, MechanicType>
        {
            ["Hot Impact"] = MechanicType.SharedTankBuster,
            ["Deep Impact"] = MechanicType.TankBuster,
            ["Pyrotation"] = MechanicType.Raidwide,
        }),
    };
}
