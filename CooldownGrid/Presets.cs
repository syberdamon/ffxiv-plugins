using System;
using System.Collections.Generic;
using System.Linq;

namespace CooldownGrid;

/// <summary>
/// Starter layouts, applied the first time you open a job (or via "Reset this job to the
/// starter layout"). Everything is editable afterwards.
/// Built from the tank crosswalk agreed with syberdamon (same slots Mit Planner uses):
///   Defensive (self):  Rampart, Big, Secondary, Short, Invuln (+ job extras)
///   Defensive (group): Reprisal + party mit(s)
///   OT Sent Mits:      the job's "sent" ability, cast on the other tank
///   Support:           Provoke, Shirk, Interject, Low Blow, Arm's Length
/// DRK's TBN and GNB's Heart of Corundum are both their Short and their sent ability, so they
/// appear in Defensive (self) and in OT Sent Mits.
/// [Assumption] Action IDs are from memory. A wrong ID simply won't appear (it won't match any
/// of the job's actions), so check the Abilities tab in game and fix anything missing.
/// Base IDs are stored; upgraded versions (e.g. Sentinel -> Guardian) show automatically.
/// </summary>
public static class Presets
{
    // ClassJob row ids
    private const uint Gladiator = 1, Paladin = 19, Marauder = 3, Warrior = 21, DarkKnight = 32, Gunbreaker = 37;

    // Role actions shared by every tank
    private const uint Rampart = 7531, Reprisal = 7535, Provoke = 7533, Shirk = 7537,
                       Interject = 7538, LowBlow = 7540, ArmsLength = 7548;

    private static readonly uint[] Support = { Provoke, Shirk, Interject, LowBlow, ArmsLength };

    public static List<Assignment> For(uint jobId) => jobId switch
    {
        Paladin or Gladiator => Build(
            self: new uint[]
            {
                Rampart,
                17,    // Sentinel -> Guardian (Big)
                22,    // Bulwark (Secondary)
                3542,  // Sheltron -> Holy Sheltron (Short)
                30,    // Hallowed Ground (Invuln)
            },
            group: new uint[]
            {
                Reprisal,
                3540,  // Divine Veil
                7385,  // Passage of Arms
            },
            sent: new uint[]
            {
                7382,  // Intervention
            }),

        Warrior or Marauder => Build(
            self: new uint[]
            {
                Rampart,
                44,    // Vengeance -> Damnation (Big)
                40,    // Thrill of Battle (Secondary)
                3551,  // Raw Intuition -> Bloodwhetting (Short)
                3552,  // Equilibrium
                43,    // Holmgang (Invuln)
            },
            group: new uint[]
            {
                Reprisal,
                7388,  // Shake It Off
            },
            sent: new uint[]
            {
                16464, // Nascent Flash
            }),

        DarkKnight => Build(
            self: new uint[]
            {
                Rampart,
                3636,  // Shadow Wall -> Shadowed Vigil (Big)
                25754, // Oblation (Secondary)
                3634,  // Dark Mind (magic only)
                7393,  // The Blackest Night (Short)
                3638,  // Living Dead (Invuln)
            },
            group: new uint[]
            {
                Reprisal,
                16471, // Dark Missionary
            },
            sent: new uint[]
            {
                7393,  // The Blackest Night, cast on the other tank
            }),

        Gunbreaker => Build(
            self: new uint[]
            {
                Rampart,
                16148, // Nebula -> Great Nebula (Big)
                16140, // Camouflage (Secondary)
                16161, // Heart of Stone -> Heart of Corundum (Short)
                16151, // Aurora (heal; not a Secondary)
                16152, // Superbolide (Invuln)
            },
            group: new uint[]
            {
                Reprisal,
                16160, // Heart of Light
            },
            sent: new uint[]
            {
                16161, // Heart of Corundum, cast on the other tank
            }),

        _ => new List<Assignment>(),
    };

    private static List<Assignment> Build(uint[] self, uint[] group, uint[] sent) =>
        new (Guid Group, uint[] Actions)[]
            {
                (Configuration.DefensiveSelfId, self),
                (Configuration.DefensiveGroupId, group),
                (Configuration.OtSentMitsId, sent),
                (Configuration.SupportId, Support),
            }
            .SelectMany(g => g.Actions.Select(a => new Assignment { ActionId = a, GroupId = g.Group }))
            .ToList();
}
