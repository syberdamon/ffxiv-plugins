using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace MaterialStash;

/// <summary>
/// Where an item comes from, so the Clean-up tab can say what getting it again would take.
/// Sources are Teamcraft's data (Data/cleanup_sources.json, refreshed by CI): duty drops, gil shops,
/// special-shop trades. On top of that, how hard each duty is to run today at level 100 unsynced:
/// a rule of thumb by content type and level, plus exceptions. The rules are [Estimate]s from
/// community sources (see README "Clean-up: sources"), not tested in game:
///  - Dungeons below the cap, normal trials and ARR-SB extremes: solo unsynced. Exception: the
///    Pool of Tribute (Extreme) needs a second player for one mechanic.
///  - ARR-SB alliance raids: unsynced with one player in each of the three alliances.
///  - ShB and later extremes and savage raids: still need a group.
/// </summary>
public static class Regain
{
    public enum Effort { Solo = 0, SmallGroup = 1, Group = 2, None = 3 }

    public sealed record Duty(uint Id, string Name, int Type, int Level);

    private static Dictionary<uint, Duty>? instances;
    private static Dictionary<uint, uint[]>? duties;
    private static Dictionary<uint, long>? gil;
    private static Dictionary<uint, long[]>? shops;
    private static Dictionary<uint, uint[]>? tokens;

    private static readonly string[] AllianceRaids =
    {
        "Labyrinth of the Ancients", "Syrcus Tower", "World of Darkness", "Void Ark", "Weeping City of Mhach", "Dun Scaith",
        "Royal City of Rabanastre", "Ridorana Lighthouse", "Orbonne Monastery", "Copied Factory", "Puppets' Bunker",
        "Tower at Paradigm's Breach", "Aglaia", "Euphrosyne", "Thaleia", "Jeuno", "San d'Oria",
    };

    private static void Load()
    {
        if (instances != null) return;
        instances = new(); duties = new(); gil = new(); shops = new(); tokens = new();
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MaterialStash.cleanup_sources.json");
            if (stream == null) return;
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            foreach (var p in root.GetProperty("instances").EnumerateObject())
            {
                var a = p.Value;
                var id = uint.Parse(p.Name);
                instances[id] = new Duty(id, a[0].GetString() ?? "", a[1].GetInt32(), a[2].GetInt32());
            }
            foreach (var p in root.GetProperty("duties").EnumerateObject())
                duties[uint.Parse(p.Name)] = p.Value.EnumerateArray().Select(x => x.GetUInt32()).ToArray();
            foreach (var p in root.GetProperty("gil").EnumerateObject())
                gil[uint.Parse(p.Name)] = p.Value.GetInt64();
            foreach (var p in root.GetProperty("shops").EnumerateObject())
                shops[uint.Parse(p.Name)] = p.Value.EnumerateArray().Select(x => x.GetInt64()).ToArray();
            foreach (var p in root.GetProperty("tokens").EnumerateObject())
                tokens[uint.Parse(p.Name)] = p.Value.EnumerateArray().Select(x => x.GetUInt32()).ToArray();
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Material Stash: couldn't read the clean-up sources"); }
    }

    public static IEnumerable<Duty> DutiesFor(uint itemId)
    {
        Load();
        return duties!.TryGetValue(itemId % 1_000_000, out var ids)
            ? ids.Select(i => instances!.GetValueOrDefault(i)).OfType<Duty>()
            : Enumerable.Empty<Duty>();
    }

    public static long GilPrice(uint itemId) { Load(); return gil!.GetValueOrDefault(itemId % 1_000_000); }

    public static (uint Currency, long Amount)? ShopCost(uint itemId)
    {
        Load();
        return shops!.TryGetValue(itemId % 1_000_000, out var c) && c.Length == 2 ? ((uint)c[0], c[1]) : null;
    }

    /// <summary>What this item buys if it's an exchange token (empty if it isn't one).</summary>
    public static uint[] TokenRewards(uint itemId) { Load(); return tokens!.GetValueOrDefault(itemId % 1_000_000) ?? Array.Empty<uint>(); }

    /// <summary>How hard a duty is to run again today (level 100, unsynced where allowed), with a short note.</summary>
    public static (Effort Effort, string How) HowHard(Duty d)
    {
        var n = d.Name;
        var high = n.Contains("(Extreme)") || n.Contains("Minstrel's Ballad") || n.Contains("(Unreal)");
        switch (d.Type)
        {
            case 2: // dungeon
                return d.Level < 100 ? (Effort.Solo, "solo unsynced") : (Effort.Group, "Duty Finder / Duty Support");
            case 4: // trial
                if (n.Contains("Pool of Tribute")) return (Effort.SmallGroup, "2 players unsynced (one mechanic needs a second person)");
                if (high) return d.Level <= 70 ? (Effort.Solo, "solo unsynced (extreme)") : (Effort.Group, "full party: still too hard to solo");
                return d.Level < 100 ? (Effort.Solo, "solo unsynced") : (Effort.Group, "Duty Finder");
            case 5: // raid
                if (AllianceRaids.Any(a => n.Contains(a, StringComparison.OrdinalIgnoreCase)))
                    return d.Level <= 70 ? (Effort.SmallGroup, "3 players unsynced (one per alliance)") : (Effort.Group, "Duty Finder (alliance raid)");
                if (n.Contains("(Savage)"))
                    return d.Level <= 50 ? (Effort.SmallGroup, "solo or a few players unsynced")
                         : d.Level <= 70 ? (Effort.SmallGroup, "small group unsynced")
                         : (Effort.Group, "full savage party");
                if (n.Contains("Second Coil") && (n.Contains("Turn 3") || n.Contains("Turn 4")))
                    return (Effort.SmallGroup, "2+ players unsynced (mechanics)");
                return d.Level <= 80 ? (Effort.Solo, "solo unsynced") : d.Level < 100 ? (Effort.SmallGroup, "small group unsynced or Duty Finder") : (Effort.Group, "Duty Finder");
            case 21: return (Effort.Solo, "deep dungeon, solo-able");
            case 30: return (Effort.Solo, "variant dungeon, solo");
            case 9: return (Effort.SmallGroup, "treasure map dungeon (luck)");
            default: return (Effort.Group, "group content");
        }
    }

    /// <summary>The easiest way back, and every way back as short lines.</summary>
    public static (Effort Easiest, List<string> Lines) Ways(uint itemId, bool marketable, bool craftable, Func<uint, string> name)
    {
        var lines = new List<string>();
        var best = Effort.None;
        if (marketable) { lines.Add("Market board"); best = Effort.Solo; }
        if (GilPrice(itemId) is > 0 and var g) { lines.Add($"Vendors sell it for {g:N0} gil"); best = Effort.Solo; }
        if (ShopCost(itemId) is { } sc)
        {
            lines.Add($"Exchange: {sc.Amount:N0} x {name(sc.Currency)}");
            if (sc.Currency < 100) best = Effort.Solo; // tomestones, seals, scrips...
        }
        if (craftable) { lines.Add("Crafted"); best = Effort.Solo; }
        foreach (var d in DutiesFor(itemId).OrderBy(d => HowHard(d).Effort).ThenBy(d => d.Level).Take(4))
        {
            var (e, how) = HowHard(d);
            lines.Add($"{d.Name} (Lv {d.Level}): {how}");
            if (e < best) best = e;
        }
        if (lines.Count == 0) lines.Add("No known source: it may be gone for good");
        return (best, lines);
    }

    public static bool HasSource(uint itemId) =>
        GilPrice(itemId) > 0 || ShopCost(itemId) != null || DutiesFor(itemId).Any();
}
