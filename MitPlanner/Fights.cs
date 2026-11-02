using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace MitPlanner;

/// <summary>A fight the plugin knows: which zone, which timeline file, and default mechanic tags.</summary>
public sealed record FightDefinition(uint TerritoryId, string Name, string Resource, IReadOnlyDictionary<string, MechanicType> DefaultTags);

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
