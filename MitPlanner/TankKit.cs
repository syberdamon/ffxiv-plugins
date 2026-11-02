using System.Collections.Generic;

namespace MitPlanner;

/// <summary>
/// One tank job's mitigation toolkit, by slot. Action IDs are base IDs; upgrades
/// (Sentinel -> Guardian, Raw Intuition -> Bloodwhetting, ...) resolve in game automatically.
/// [Assumption] IDs are from memory (Dawntrail 7.x). The plan generator skips any ID the
/// current job doesn't actually have and reports it, so a wrong ID shows up as "missing".
/// Reviewed with syberdamon: Aurora is not a secondary; invulns are never auto-planned.
/// </summary>
public sealed record TankKit(
    string Job,
    uint Big,          // ~40%, 120s: first choice on a buster when it's back in time
    uint Rampart,      // role 20%, 90s: when Big isn't available...
    uint Secondary,    // ...paired with this
    uint Short,        // stacked on every buster
    uint Sent,         // the Short-style mit cast on the other tank (OT Sent Mits)
    uint Invuln,       // never auto-planned
    bool ShortUsesResource); // Sheltron (Oath gauge) / TBN (MP): QC can't see the resource

public static class TankKits
{
    private const uint RampartId = 7531;

    // ClassJob row ids: GLA 1 / PLD 19, MRD 3 / WAR 21, DRK 32, GNB 37.
    private static readonly TankKit Paladin = new("Paladin",
        Big: 17,          // Sentinel -> Guardian
        Rampart: RampartId,
        Secondary: 22,    // Bulwark
        Short: 3542,      // Sheltron -> Holy Sheltron
        Sent: 7382,       // Intervention
        Invuln: 30,       // Hallowed Ground
        ShortUsesResource: true);

    private static readonly TankKit Warrior = new("Warrior",
        Big: 44,          // Vengeance -> Damnation
        Rampart: RampartId,
        Secondary: 40,    // Thrill of Battle
        Short: 3551,      // Raw Intuition -> Bloodwhetting
        Sent: 16464,      // Nascent Flash
        Invuln: 43,       // Holmgang
        ShortUsesResource: false);

    private static readonly TankKit DarkKnight = new("Dark Knight",
        Big: 3636,        // Shadow Wall -> Shadowed Vigil
        Rampart: RampartId,
        Secondary: 25754, // Oblation (works on physical and magic; Dark Mind is magic-only)
        Short: 7393,      // The Blackest Night
        Sent: 7393,       // The Blackest Night, cast on the MT
        Invuln: 3638,     // Living Dead
        ShortUsesResource: true);

    private static readonly TankKit Gunbreaker = new("Gunbreaker",
        Big: 16148,       // Nebula -> Great Nebula
        Rampart: RampartId,
        Secondary: 16140, // Camouflage
        Short: 16161,     // Heart of Stone -> Heart of Corundum
        Sent: 16161,      // Heart of Corundum, cast on the MT
        Invuln: 16152,    // Superbolide
        ShortUsesResource: false);

    private static readonly Dictionary<uint, TankKit> ByJob = new()
    {
        [1] = Paladin, [19] = Paladin,
        [3] = Warrior, [21] = Warrior,
        [32] = DarkKnight,
        [37] = Gunbreaker,
    };

    public static TankKit? For(uint classJobId) => ByJob.GetValueOrDefault(classJobId);
}
