using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace MaterialStash;

public enum StashKind { None, Gathered, MobDrop, Crystal, Manual }

/// <summary>An item and why it qualifies (or not).</summary>
public sealed record ItemInfo(uint ItemId, string Name, uint CategoryId, string Category, int StackSize, uint Icon, StashKind Rule, string Why);

/// <summary>
/// Which items count as stashable crafting materials, from the game's own data:
///  - Gathered: obtainable from a mining/botany node, fishing or spearfishing, and used in a recipe.
///  - Mob drop: used in a recipe, not made by a recipe, tradeable, common (white) rarity, not gear.
///    That covers hides, bones, fleece, reagents...; boss and glamour-weapon mats are almost always
///    untradeable or green/blue, so they fall out.
///  - Crystals, shards, clusters (item ids 2-19): their own tab, handled separately.
/// Collectables never qualify. The include/exclude lists override everything.
/// </summary>
public static class Items
{
    public const uint FirstCrystal = 2, LastCrystal = 19;
    public const int CrystalCap = 9999;
    public static bool IsCrystal(uint id) => id % 1_000_000 is >= FirstCrystal and <= LastCrystal;

    private static Dictionary<uint, ItemInfo>? cache;

    public static ItemInfo? Info(uint itemId)
    {
        var id = itemId % 1_000_000;
        var all = Build();
        if (all.TryGetValue(id, out var info)) return info;
        // Not a candidate under the rules; still describe it (for the include list / preview).
        var row = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(id);
        if (row == null || id == 0) return null;
        var r = row.Value;
        var cat = r.ItemUICategory.ValueNullable;
        var made = new ItemInfo(id, r.Name.ExtractText(), r.ItemUICategory.RowId, cat?.Name.ExtractText() ?? "?", (int)r.StackSize, r.Icon, StashKind.None, WhyNot(r));
        all[id] = made;
        return made;
    }

    /// <summary>Does this item go to the retainers under the current settings? (Crystals: no, they're handled apart.)</summary>
    public static bool Stashable(uint itemId, Configuration c)
    {
        var id = itemId % 1_000_000;
        if (IsCrystal(id)) return false;
        if (c.Exclude.Contains(id)) return false;
        if (c.Include.Contains(id)) return true;
        return Info(id)?.Rule switch
        {
            StashKind.Gathered => c.IncludeGathered,
            StashKind.MobDrop => c.IncludeMobDrops,
            _ => false,
        };
    }

    /// <summary>All items the rules accept (for the category list).</summary>
    public static IEnumerable<ItemInfo> Candidates() => Build().Values.Where(i => i.Rule is StashKind.Gathered or StashKind.MobDrop);

    private static Dictionary<uint, ItemInfo> Build()
    {
        if (cache != null) return cache;
        var dm = Plugin.DataManager;
        var gathered = new HashSet<uint>();
        foreach (var g in dm.GetExcelSheet<GatheringItem>()) if (g.Item.RowId != 0) gathered.Add(g.Item.RowId);
        foreach (var s in dm.GetExcelSheet<SpearfishingItem>()) if (s.Item.RowId != 0) gathered.Add(s.Item.RowId);
        foreach (var f in dm.GetExcelSheet<FishParameter>()) if (f.Item.RowId != 0) gathered.Add(f.Item.RowId);

        var ingredients = new HashSet<uint>();
        var crafted = new HashSet<uint>();
        foreach (var r in dm.GetExcelSheet<Recipe>())
        {
            if (r.ItemResult.RowId == 0) continue;
            crafted.Add(r.ItemResult.RowId);
            foreach (var ing in r.Ingredient) if (ing.RowId != 0) ingredients.Add(ing.RowId);
        }

        var items = dm.GetExcelSheet<Item>();
        var result = new Dictionary<uint, ItemInfo>();
        foreach (var id in ingredients.Concat(gathered).Distinct())
        {
            if (items.GetRowOrDefault(id) is not { } r || r.Name.ExtractText().Length == 0) continue;
            var kind = StashKind.None;
            var why = "";
            if (IsCrystal(id)) { kind = StashKind.Crystal; why = "crystal"; }
            else if (r.IsCollectable) why = "collectable";
            else if (!ingredients.Contains(id)) why = "not used in any recipe";
            else if (gathered.Contains(id)) { kind = StashKind.Gathered; why = "gathered"; }
            else if (crafted.Contains(id)) why = "crafted (made by a recipe)";
            else if (r.IsUntradable) why = "untradeable";
            else if (r.Rarity != 1) why = "not common rarity";
            else if (r.EquipSlotCategory.RowId != 0) why = "gear";
            else { kind = StashKind.MobDrop; why = "recipe material, not gathered"; }
            var cat = r.ItemUICategory.ValueNullable;
            result[id] = new ItemInfo(id, r.Name.ExtractText(), r.ItemUICategory.RowId, cat?.Name.ExtractText() ?? "?", (int)r.StackSize, r.Icon, kind, why);
        }
        return cache = result;
    }

    private static string WhyNot(Item r) =>
        r.IsCollectable ? "collectable" : r.EquipSlotCategory.RowId != 0 ? "gear" : "not a recipe material";

    public static string KindLabel(StashKind k) => k switch
    {
        StashKind.Gathered => "gathered",
        StashKind.MobDrop => "mob drop",
        StashKind.Crystal => "crystal",
        StashKind.Manual => "included by you",
        _ => "-",
    };
}
