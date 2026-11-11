using System.Collections.Generic;
using System.Linq;

namespace MitPlanner;

/// <summary>
/// One healer job's tools for planned mechanics, best first. IDs checked against the game's
/// Action sheet (xivapi ffxiv-datamining, 2026-11-10); base IDs, upgrades resolve in game
/// (Succor -> Concitation, Aspected Helios -> Helios Conjunction, Eukrasian Prognosis -> II).
/// [Assumption] The picks and their order are a first cut, agreed in outline with syberdamon
/// (shield/mit the tank before busters, party mit before raid-wides); tune after real pulls.
/// </summary>
public sealed record HealerKit(
    string Job,
    uint[] Buster,        // off-GCD tank shields/mits, cast on the tank, best first
    uint[] BusterGcd,     // GCD tank shield: planned only when no off-GCD one is back
    uint[] Raidwide,      // off-GCD party mits, best first
    uint[] RaidwideGcd,   // GCD party shield: planned only when no off-GCD one is back
    string? ResourceNote);

public static class HealerKits
{
    // ClassJob row ids: CNJ 6 / WHM 24, (ACN 26 ->) SCH 28, AST 33, SGE 40.
    private static readonly HealerKit WhiteMage = new("White Mage",
        Buster: new uint[] { 7432, 25861 },        // Divine Benison, Aquaveil
        BusterGcd: System.Array.Empty<uint>(),     // WHM has no GCD tank shield
        Raidwide: new uint[] { 16536, 25862, 3569 }, // Temperance, Liturgy of the Bell, Asylum
        RaidwideGcd: System.Array.Empty<uint>(),
        ResourceNote: null);

    private static readonly HealerKit Scholar = new("Scholar",
        Buster: new uint[] { 25867, 7434 },        // Protraction, Excogitation
        BusterGcd: new uint[] { 185 },             // Adloquium
        Raidwide: new uint[] { 188, 25868, 16538 }, // Sacred Soil, Expedient, Fey Illumination
        RaidwideGcd: new uint[] { 186 },           // Succor (-> Concitation)
        ResourceNote: "Sacred Soil and Excogitation cost Aetherflow, which the cooldown check can't see; keep stacks for them.");

    private static readonly HealerKit Astrologian = new("Astrologian",
        Buster: new uint[] { 25873, 16556 },       // Exaltation, Celestial Intersection
        BusterGcd: new uint[] { 3595 },            // Aspected Benefic
        Raidwide: new uint[] { 3613, 7439 },       // Collective Unconscious, Earthly Star
        RaidwideGcd: new uint[] { 3601 },          // Aspected Helios (-> Helios Conjunction)
        ResourceNote: null);

    private static readonly HealerKit Sage = new("Sage",
        Buster: new uint[] { 24305, 24303 },       // Haima, Taurochole
        BusterGcd: new uint[] { 24291 },           // Eukrasian Diagnosis (Eukrasia first)
        Raidwide: new uint[] { 24298, 24302, 24310, 24311 }, // Kerachole, Physis II, Holos, Panhaima
        RaidwideGcd: new uint[] { 24292 },         // Eukrasian Prognosis (Eukrasia first)
        ResourceNote: "Kerachole and Taurochole cost Addersgall, which the cooldown check can't see; keep stacks for them.");

    private static readonly Dictionary<uint, HealerKit> ByJob = new()
    {
        [6] = WhiteMage, [24] = WhiteMage,
        [28] = Scholar,
        [33] = Astrologian,
        [40] = Sage,
    };

    public static HealerKit? For(uint classJobId) => ByJob.GetValueOrDefault(classJobId);
    public static bool IsHealer(uint classJobId) => ByJob.ContainsKey(classJobId);

    // ---- Abilities that need another button first.
    public const uint Eukrasia = 24290;
    public const uint EarthlyStar = 7439;

    /// <summary>
    /// The button that casts it. Eukrasian spells aren't on a hotbar of their own: after Eukrasia,
    /// Diagnosis turns into Eukrasian Diagnosis and Prognosis into Eukrasian Prognosis (II).
    /// </summary>
    public static uint PressId(uint actionId) => actionId switch
    {
        24291 => 24284, // Eukrasian Diagnosis  <- Diagnosis
        24292 => 24286, // Eukrasian Prognosis  <- Prognosis
        37034 => 24286, // Eukrasian Prognosis II <- Prognosis
        _ => actionId,
    };

    /// <summary>A GCD that must go first (Eukrasia), or 0.</summary>
    public static uint Prereq(uint actionId) => PressId(actionId) != actionId ? Eukrasia : 0;

    /// <summary>
    /// Earthly Star: placed this long before it's detonated so it has matured into Giant
    /// Dominance (stronger heal). Pressing Earthly Star again detonates it.
    /// </summary>
    public static float MatureSeconds(uint actionId) => actionId == EarthlyStar ? 10f : 0f;

    /// <summary>GCD spells the catalog adds for healers (it normally lists cooldowns only).</summary>
    public static IEnumerable<uint> CatalogGcds(uint classJobId) =>
        For(classJobId) is { } k ? k.BusterGcd.Concat(k.RaidwideGcd) : Enumerable.Empty<uint>();
}
