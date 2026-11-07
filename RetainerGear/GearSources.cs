using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace RetainerGear;

public enum SourceKind { Crafted, Dungeon, Tomestone }

/// <summary>One way to get an item: crafted, a dungeon drop, or bought with a currency.</summary>
public sealed record GearSource(SourceKind Kind, string Text, uint Currency = 0, int Cost = 0);

/// <summary>A piece of gear a retainer could wear, with how to get it.</summary>
public sealed record GearOption(uint ItemId, string Name, int ItemLevel, int Slot, bool Unique, bool TwoHanded,
    bool CanBeHq, uint Icon, List<GearSource> Sources);

/// <summary>
/// Where gear comes from. Crafted = game data (recipes). Dungeon drops and shop trades = Teamcraft's
/// data (bundled, MIT), plus drops learned as you play. Raid/savage gear isn't in any of these.
/// </summary>
public sealed class GearSources
{
    private readonly Configuration config;
    private readonly HashSet<uint> crafted = new();
    private readonly Dictionary<uint, List<int>> dungeonDrops = new();
    private readonly Dictionary<int, (string Name, int Level)> dungeons = new();
    private readonly Dictionary<uint, List<(uint Currency, int Amount)>> shop = new();
    private readonly Dictionary<string, List<GearOption>> cache = new();

    public string DataNote { get; } = "";

    public GearSources(Configuration config)
    {
        this.config = config;
        try
        {
            foreach (var r in Plugin.DataManager.GetExcelSheet<Recipe>())
                if (r.ItemResult.RowId != 0) crafted.Add(r.ItemResult.RowId);

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RetainerGear.gear_sources.json")!;
            using var doc = JsonDocument.Parse(stream);
            foreach (var p in doc.RootElement.GetProperty("dungeon").EnumerateObject())
                dungeonDrops[uint.Parse(p.Name)] = p.Value.EnumerateArray().Select(v => v.GetInt32()).ToList();
            foreach (var p in doc.RootElement.GetProperty("instances").EnumerateObject())
                dungeons[int.Parse(p.Name)] = (p.Value.GetProperty("name").GetString() ?? "?", p.Value.GetProperty("level").GetInt32());
            foreach (var p in doc.RootElement.GetProperty("shop").EnumerateObject())
                shop[uint.Parse(p.Name)] = p.Value.EnumerateArray().Select(v => ((uint)v[0].GetInt32(), v[1].GetInt32())).ToList();
            DataNote = $"{crafted.Count} recipes, {dungeonDrops.Count} dungeon drops, {shop.Count} shop items";
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "RetainerGear: couldn't load gear sources");
            DataNote = "Gear source data failed to load (see /xllog)";
        }
    }

    public void Invalidate() => cache.Clear();

    /// <summary>Currency item ids from the names in settings.</summary>
    public Dictionary<uint, string> Currencies()
    {
        var wanted = new HashSet<string>(config.Currencies, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<uint, string>();
        foreach (var it in Plugin.DataManager.GetExcelSheet<Item>(ClientLanguage.English))
        {
            if (it.RowId > 100) break;   // currencies are low ids
            var n = it.Name.ExtractText();
            if (wanted.Contains(n)) result[it.RowId] = n.Replace("Allagan Tomestone of ", "");
        }
        return result;
    }

    /// <summary>All gear this job can wear at this level, from the enabled sources, best first per slot.</summary>
    public List<GearOption> For(uint classJob, int level)
    {
        var key = $"{classJob}:{level}:{config.UseCrafted}{config.UseDungeon}{config.UseTomestones}:{string.Join(",", config.Currencies)}:{config.LearnedDrops.Count}";
        if (cache.TryGetValue(key, out var hit)) return hit;

        var job = Plugin.DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault(classJob);
        var abbr = job?.Abbreviation.ExtractText() ?? "";
        var currencies = Currencies();
        var categoryProp = typeof(ClassJobCategory).GetProperty(abbr, BindingFlags.Public | BindingFlags.Instance);
        var result = new List<GearOption>();
        if (categoryProp == null) { cache[key] = result; return result; }

        var learned = config.LearnedDrops.ToDictionary(k => k.Key, k => k.Value);
        foreach (var it in Plugin.DataManager.GetExcelSheet<Item>(ClientLanguage.English))
        {
            var esc = it.EquipSlotCategory.ValueNullable;
            if (esc == null || it.EquipSlotCategory.RowId == 0) continue;
            if (it.LevelEquip > level || it.EquipRestriction.RowId > 1) continue;   // race/sex-locked gear skipped
            if (it.ClassJobCategory.ValueNullable is not { } cat || categoryProp.GetValue(cat) is not true) continue;
            var slot = SlotOf(esc.Value);
            if (slot < 0) continue;

            var sources = new List<GearSource>();
            if (config.UseCrafted && crafted.Contains(it.RowId)) sources.Add(new(SourceKind.Crafted, "Crafted"));
            if (config.UseDungeon)
            {
                if (dungeonDrops.TryGetValue(it.RowId, out var ids))
                    foreach (var d in ids)
                        if (dungeons.TryGetValue(d, out var dn)) sources.Add(new(SourceKind.Dungeon, $"{dn.Name} (Lv{dn.Level})"));
                if (learned.TryGetValue(it.RowId, out var terr) && sources.All(s => s.Kind != SourceKind.Dungeon))
                    sources.Add(new(SourceKind.Dungeon, TerritoryName(terr) + " (seen)"));
            }
            if (config.UseTomestones && shop.TryGetValue(it.RowId, out var trades))
                foreach (var (cur, amt) in trades)
                    if (currencies.TryGetValue(cur, out var cn)) sources.Add(new(SourceKind.Tomestone, $"{amt} {cn}", cur, amt));
            if (sources.Count == 0) continue;

            result.Add(new GearOption(it.RowId, it.Name.ExtractText(), (int)it.LevelItem.RowId, slot, it.IsUnique,
                esc.Value.OffHand == -1, it.CanBeHq, it.Icon, sources));
        }
        result = result.OrderBy(o => o.Slot).ThenByDescending(o => o.ItemLevel).ToList();
        cache[key] = result;
        return result;
    }

    private static string TerritoryName(uint territory) =>
        Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.PlaceName.ValueNullable?.Name.ExtractText() ?? $"zone {territory}";

    /// <summary>Equip slot index used by the retainer's equipment container (12 = ring, either hand).</summary>
    public static int SlotOf(EquipSlotCategory c)
    {
        if (c.MainHand == 1) return 0;
        if (c.OffHand == 1) return 1;
        if (c.Head == 1) return 2;
        if (c.Body == 1) return 3;
        if (c.Gloves == 1) return 4;
        if (c.Legs == 1) return 6;
        if (c.Feet == 1) return 7;
        if (c.Ears == 1) return 8;
        if (c.Neck == 1) return 9;
        if (c.Wrists == 1) return 10;
        if (c.FingerL == 1 || c.FingerR == 1) return 12;
        return -1;
    }

    public static readonly string[] SlotNames =
        { "Main hand", "Off hand", "Head", "Body", "Hands", "Waist", "Legs", "Feet", "Ears", "Neck", "Wrists", "Ring", "Ring", "Soul" };
}
