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
///   OT Sent Mits:      the job's "sent" ability + Shirk, both cast on the other tank
///   Support:           Provoke, Interject, Low Blow, Arm's Length, Sprint, Limit Break
/// Every other class gets Sprint + Limit Break in Support.
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
    public const uint Shirk = 7537;
    private const uint Rampart = 7531, Reprisal = 7535, Provoke = 7533,
                       Interject = 7538, LowBlow = 7540, ArmsLength = 7548;

    // Every class: Sprint (action 3) and Limit Break, in Support.
    private const uint Sprint = 3;
    private static readonly uint[] Everyone = { Sprint, ActionCatalog.LimitBreakId };
    public static IReadOnlyList<uint> EveryoneIds => Everyone;

    // Healers
    private const uint Conjurer = 6, WhiteMage = 24, Arcanist = 26, Scholar = 28, Astrologian = 33, Sage = 40;
    public static readonly uint[] HealerJobs = { Conjurer, WhiteMage, Scholar, Astrologian, Sage };
    private const uint Swiftcast = 7561, LucidDreaming = 7562, Surecast = 7559, Rescue = 7571;
    private static readonly uint[] HealerSupport = { Swiftcast, LucidDreaming, Surecast, Rescue, Sprint, ActionCatalog.LimitBreakId };
    private static readonly uint[] Support = { Provoke, Interject, LowBlow, ArmsLength, Sprint, ActionCatalog.LimitBreakId };

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
                Shirk, // always on the other tank (sheds your enmity to them)
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
                Shirk, // always on the other tank (sheds your enmity to them)
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
                Shirk, // always on the other tank (sheds your enmity to them)
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
                Shirk, // always on the other tank (sheds your enmity to them)
            }),

        // ---- Healers. "Tank" = cast on whoever is tanking (no targeting needed); GCD upkeep spells
        // (Regen, Aspected Benefic) are there so the grid can pulse them when they fall off.
        WhiteMage or Conjurer => BuildHealer(
            tank: new uint[]
            {
                7432,  // Divine Benison
                25861, // Aquaveil
                3570,  // Tetragrammaton
                140,   // Benediction
                137,   // Regen (GCD; pulses when it's not on the tank)
            },
            group: new uint[]
            {
                16536, // Temperance
                7433,  // Plenary Indulgence
                3569,  // Asylum
                25862, // Liturgy of the Bell
            },
            extra: new uint[]
            {
                136,   // Presence of Mind
                7430,  // Thin Air
                3571,  // Assize
            }),

        Scholar => BuildHealer(
            tank: new uint[]
            {
                25867, // Protraction
                7434,  // Excogitation
                189,   // Lustrate
                7437,  // Aetherpact
            },
            group: new uint[]
            {
                188,   // Sacred Soil
                25868, // Expedient
                16538, // Fey Illumination
                3583,  // Indomitability
                16537, // Whispering Dawn
                16545, // Summon Seraph
                37014, // Seraphism
            },
            extra: new uint[]
            {
                166,   // Aetherflow
                7436,  // Chain Stratagem
                16542, // Recitation
                3587,  // Dissipation
                3585,  // Deployment Tactics
                3586,  // Emergency Tactics
            }),

        Astrologian => BuildHealer(
            tank: new uint[]
            {
                3614,  // Essential Dignity
                16556, // Celestial Intersection
                25873, // Exaltation
                3612,  // Synastry
                3595,  // Aspected Benefic (GCD; pulses when it's not on the tank)
            },
            group: new uint[]
            {
                16559, // Neutral Sect
                3613,  // Collective Unconscious
                7439,  // Earthly Star
                16557, // Horoscope
                25874, // Macrocosmos
                16553, // Celestial Opposition
            },
            extra: new uint[]
            {
                3606,  // Lightspeed
                16552, // Divination
            }),

        Sage => BuildHealer(
            tank: new uint[]
            {
                24285, // Kardia (pulses when Kardion isn't on the tank)
                24303, // Taurochole
                24296, // Druochole
                24305, // Haima
                24317, // Krasis
            },
            group: new uint[]
            {
                24298, // Kerachole
                24310, // Holos
                24311, // Panhaima
                24302, // Physis II
                24299, // Ixochole
                37035, // Philosophia
            },
            extra: new uint[]
            {
                24300, // Zoe
                24309, // Rhizomata
                24294, // Soteria
            }),

        // Everyone else: Sprint + Limit Break in Support (LB is skipped for crafters/gatherers).
        _ => Everyone.Select(a => new Assignment { ActionId = a, GroupId = Configuration.SupportId }).ToList(),
    };

    private static List<Assignment> BuildHealer(uint[] tank, uint[] group, uint[] extra) =>
        new (Guid Group, uint[] Actions)[]
            {
                (Configuration.TankId, tank),
                (Configuration.DefensiveGroupId, group),
                (Configuration.SupportId, extra.Concat(HealerSupport).ToArray()),
            }
            .SelectMany(g => g.Actions.Select(a => new Assignment { ActionId = a, GroupId = g.Group }))
            .ToList();

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
