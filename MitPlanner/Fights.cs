using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace MitPlanner;

/// <summary>A fight the plugin knows: which zone, which timeline file, and default mechanic tags.</summary>
public sealed record FightDefinition(
    uint TerritoryId,
    string Name,
    string Resource,
    IReadOnlyDictionary<string, MechanicType> DefaultTags,
    IReadOnlyDictionary<string, uint>? HeadMarkers = null);  // label -> head marker that marks the target

public static class Fights
{
    /// <summary>
    /// Bundled fights. Timelines come from cactbot (Apache-2.0); default tags come from cactbot's
    /// trigger file for the same fight (Responses.tankBuster / aoe / stack...). Tags are editable in game.
    /// </summary>
    public static readonly IReadOnlyList<FightDefinition> All = new[]
    {
        // ui/raidboss/data/05-shb/trial/ruby_weapon.txt + ruby_weapon.ts; ZoneId.CinderDrift = 897
        new FightDefinition(897, "The Cinder Drift", "MitPlanner.Timelines.ruby_weapon.txt", new Dictionary<string, MechanicType>
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

        // ui/raidboss/data/05-shb/trial/emerald_weapon.txt + emerald_weapon.ts; ZoneId.CastrumMarinum = 934
        // Tank busters: Emerald Shot (cast on the tank), Divide Et Impera (head marker 00DA on the tank).
        new FightDefinition(934, "Castrum Marinum", "MitPlanner.Timelines.emerald_weapon.txt", new Dictionary<string, MechanicType>
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
        HeadMarkers: new Dictionary<string, uint>
        {
            ["Divide Et Impera"] = 0x00DA,
        }),
    };

    private static readonly Dictionary<uint, Timeline> Cache = new();

    public static FightDefinition? ForTerritory(uint territoryId)
    {
        foreach (var f in All) if (f.TerritoryId == territoryId) return f;
        return null;
    }

    public static Timeline Load(FightDefinition fight)
    {
        if (Cache.TryGetValue(fight.TerritoryId, out var t)) return t;

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(fight.Resource)
            ?? throw new FileNotFoundException($"Missing embedded timeline {fight.Resource}");
        using var reader = new StreamReader(stream);
        t = Timeline.Parse(fight.Name, reader.ReadToEnd());
        Cache[fight.TerritoryId] = t;
        return t;
    }
}
