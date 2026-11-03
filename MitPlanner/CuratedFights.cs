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
    };
}
