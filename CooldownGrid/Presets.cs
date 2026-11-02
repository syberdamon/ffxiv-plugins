using System;
using System.Collections.Generic;
using System.Linq;

namespace CooldownGrid;

/// <summary>
/// Starter layouts, applied the first time you open a job. Everything is editable afterwards.
/// [Assumption] Action IDs are from memory. A wrong ID simply won't appear (it won't match any
/// of the job's actions), so check the Abilities tab in game and fix anything missing.
/// Base IDs are stored; upgraded versions (e.g. Vengeance -> Damnation) show automatically.
/// </summary>
public static class Presets
{
    private const uint Marauder = 3, Warrior = 21;

    public static List<Assignment> For(uint jobId) => jobId switch
    {
        Warrior or Marauder => Build(
            (Configuration.DefensiveSelfId, new uint[]
            {
                7531,  // Rampart
                44,    // Vengeance (-> Damnation)
                40,    // Thrill of Battle
                3551,  // Raw Intuition (-> Bloodwhetting)
                3552,  // Equilibrium
                43,    // Holmgang
            }),
            (Configuration.DefensiveGroupId, new uint[]
            {
                7388,  // Shake It Off
                7535,  // Reprisal
                16464, // Nascent Flash
            }),
            (Configuration.SupportId, new uint[]
            {
                7533,  // Provoke
                7537,  // Shirk
                7538,  // Interject
                7540,  // Low Blow
                7548,  // Arm's Length
            })),
        _ => new List<Assignment>(),
    };

    private static List<Assignment> Build(params (Guid Group, uint[] Actions)[] groups) =>
        groups.SelectMany(g => g.Actions.Select(a => new Assignment { ActionId = a, GroupId = g.Group })).ToList();
}
