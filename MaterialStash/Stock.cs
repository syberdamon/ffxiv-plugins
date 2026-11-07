using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace MaterialStash;

/// <summary>A recipe as the stock list needs it.</summary>
public sealed record RecipeInfo(uint RecipeId, uint ResultId, string Name, string Job, int Yield, List<(uint ItemId, int Amount)> Ingredients);

/// <summary>One stock line after recipes are added in: what to keep and what you have.</summary>
public sealed record StockNeed(uint ItemId, string Name, int Min, int Target, int Have, int Incoming, bool Crystal = false)
{
    public int Total => Have + Incoming;
    public bool BelowMin => Min > 0 && Total < Min;
    public bool BelowTarget => Total < Target;
}

/// <summary>
/// The stock list: items with a minimum and a target, plus recipes turned into their materials.
/// "Have" counts your bags, saddlebag (as last seen) and every retainer (as last seen), NQ and HQ
/// together; crystals from the crystal tabs.
/// </summary>
public static class Stock
{
    private static Dictionary<uint, RecipeInfo>? recipes;
    private static Dictionary<uint, uint>? recipeFor; // result item -> first recipe making it

    public static RecipeInfo? Recipe(uint recipeId)
    {
        BuildRecipes();
        return recipes!.TryGetValue(recipeId, out var r) ? r : null;
    }

    public static RecipeInfo? RecipeMaking(uint itemId)
    {
        BuildRecipes();
        return recipeFor!.TryGetValue(itemId % 1_000_000, out var id) ? Recipe(id) : null;
    }

    public static IEnumerable<RecipeInfo> SearchRecipes(string text)
    {
        BuildRecipes();
        if (text.Trim().Length < 2) return Enumerable.Empty<RecipeInfo>();
        return recipes!.Values.Where(r => r.Name.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Name.Length).ThenBy(r => r.Name).Take(25);
    }

    /// <summary>Items worth stocking: recipe materials and anything a venture brings back.</summary>
    public static IEnumerable<ItemInfo> SearchItems(string text)
    {
        if (text.Trim().Length < 2) return Enumerable.Empty<ItemInfo>();
        var t = text.Trim();
        var ids = new HashSet<uint>();
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        foreach (var row in sheet)
        {
            if (ids.Count >= 25) break;
            if (row.RowId == 0) continue;
            var name = row.Name.ExtractText();
            if (name.Length == 0 || !name.Contains(t, StringComparison.OrdinalIgnoreCase)) continue;
            if (Items.Info(row.RowId) is { } info && (info.Rule != StashKind.None || Ventures.For(row.RowId).Count > 0 || IsIngredient(row.RowId)))
                ids.Add(row.RowId);
        }
        return ids.Select(id => Items.Info(id)!).OrderBy(i => i.Name.Length).ThenBy(i => i.Name);
    }

    private static HashSet<uint>? ingredientSet;
    private static bool IsIngredient(uint id)
    {
        BuildRecipes();
        return ingredientSet!.Contains(id);
    }

    private static void BuildRecipes()
    {
        if (recipes != null) return;
        var map = new Dictionary<uint, RecipeInfo>();
        var first = new Dictionary<uint, uint>();
        var ing = new HashSet<uint>();
        try
        {
            var dm = Plugin.DataManager;
            var items = dm.GetExcelSheet<Item>();
            foreach (var r in dm.GetExcelSheet<Recipe>())
            {
                if (r.ItemResult.RowId == 0 || items.GetRowOrDefault(r.ItemResult.RowId) is not { } result) continue;
                var list = new List<(uint, int)>();
                for (var i = 0; i < r.Ingredient.Count && i < r.AmountIngredient.Count; i++)
                {
                    var id = r.Ingredient[i].RowId;
                    int amount = r.AmountIngredient[i];
                    if (id == 0 || amount == 0) continue;
                    list.Add((id, amount));
                    ing.Add(id);
                }
                var job = r.CraftType.ValueNullable?.Name.ExtractText() ?? "";
                map[r.RowId] = new RecipeInfo(r.RowId, r.ItemResult.RowId, result.Name.ExtractText(), job, Math.Max(1, (int)r.AmountResult), list);
                if (!first.ContainsKey(r.ItemResult.RowId)) first[r.ItemResult.RowId] = r.RowId;
            }
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "Material Stash: reading recipes failed"); }
        recipes = map;
        recipeFor = first;
        ingredientSet = ing;
    }

    /// <summary>Every item the stock list asks for, with recipes broken down: item -> (min, target).</summary>
    public static Dictionary<uint, (int Min, int Target)> Wanted(Configuration c)
    {
        var acc = new Dictionary<uint, (int Min, int Target)>();
        void Add(uint id, int min, int target)
        {
            id %= 1_000_000;
            var cur = acc.GetValueOrDefault(id);
            acc[id] = (cur.Min + min, cur.Target + target);
        }
        foreach (var s in c.StockItems) Add(s.ItemId, s.Min, Math.Max(s.Min, s.Target));
        foreach (var r in c.StockRecipes)
        {
            if (Recipe(r.RecipeId) is not { } info) continue;
            var minNeed = new Dictionary<uint, int>();
            var targetNeed = new Dictionary<uint, int>();
            Expand(info, r.Min, r.KeepAsIs, minNeed, 0);
            Expand(info, Math.Max(r.Min, r.Target), r.KeepAsIs, targetNeed, 0);
            foreach (var id in minNeed.Keys.Union(targetNeed.Keys)) Add(id, minNeed.GetValueOrDefault(id), targetNeed.GetValueOrDefault(id));
        }
        return acc;
    }

    /// <summary>Materials for `units` of a recipe's result (crafts = units / yield, rounded up).</summary>
    public static void Expand(RecipeInfo r, int units, HashSet<uint> keepAsIs, Dictionary<uint, int> acc, int depth)
    {
        if (units <= 0) return;
        var crafts = (units + r.Yield - 1) / r.Yield;
        foreach (var (id, amount) in r.Ingredients)
        {
            var need = crafts * amount;
            if (depth < 6 && !Items.IsCrystal(id) && !keepAsIs.Contains(id) && RecipeMaking(id) is { } sub)
                Expand(sub, need, keepAsIs, acc, depth + 1);
            else acc[id] = acc.GetValueOrDefault(id) + need;
        }
    }

    /// <summary>Crafted ingredients anywhere under a recipe (for the "stock as is" choices).</summary>
    public static List<uint> CraftedIngredients(RecipeInfo r, int depth = 0)
    {
        var list = new List<uint>();
        if (depth > 5) return list;
        foreach (var (id, _) in r.Ingredients)
        {
            if (Items.IsCrystal(id) || RecipeMaking(id) is not { } sub) continue;
            list.Add(id);
            list.AddRange(CraftedIngredients(sub, depth + 1));
        }
        return list.Distinct().ToList();
    }

    /// <summary>How many of an item you have (bags + saddlebag + retainers, NQ + HQ).</summary>
    public static int Have(uint itemId, CharacterData me)
    {
        var id = itemId % 1_000_000;
        if (Items.IsCrystal(id))
        {
            var mine = Tracker.Crystals(InventoryType.Crystals).TryGetValue(id, out var m) ? m.Qty : 0;
            return mine + me.Retainers.Values.Sum(r => r.Crystals.GetValueOrDefault(id));
        }
        var hq = id + 1_000_000;
        var bags = Tracker.Stacks(Tracker.Bags, x => x == id || x == hq).Sum(s => s.Qty);
        var saddle = me.Saddlebag.GetValueOrDefault(id) + me.Saddlebag.GetValueOrDefault(hq);
        var retainers = me.Retainers.Values.Sum(r => r.Items.GetValueOrDefault(id) + r.Items.GetValueOrDefault(hq));
        return bags + saddle + retainers;
    }

    /// <summary>The stock list with counts, most urgent first (under minimum by % short, then by the chosen rule).</summary>
    public static List<StockNeed> Needs(Configuration c, CharacterData me, Dictionary<uint, int> incoming)
    {
        var list = new List<StockNeed>();
        var wanted = Wanted(c);
        foreach (var (id, (min, target)) in wanted)
        {
            if (Items.IsCrystal(id) && c.CrystalStockOn) continue; // folded into the crystal lines below
            list.Add(new StockNeed(id, Items.Info(id)?.Name ?? $"#{id}", min, target, Have(id, me), incoming.GetValueOrDefault(id), Items.IsCrystal(id)));
        }
        if (c.CrystalStockOn)
        {
            // Crystal tabs hold 9,999 of a type: yours plus each retainer's ("never use" ones too: they still hold them).
            var capacity = (me.Order.Count + 1) * Items.CrystalCap;
            for (var id = Items.FirstCrystal; id <= Items.LastCrystal; id++)
            {
                var cs = c.CrystalStockFor(id);
                var fromLists = wanted.GetValueOrDefault(id); // recipes / items asking for this crystal add on top
                var min = cs.Min + fromLists.Min;
                var target = cs.Target <= 0 ? capacity : Math.Max(min, cs.Target + fromLists.Target); // 0 = no limit
                list.Add(new StockNeed(id, Items.Info(id)?.Name ?? $"#{id}", min, Math.Min(target, capacity), Have(id, me), incoming.GetValueOrDefault(id), true));
            }
        }
        return Order(list, c.AfterMinimumMode, c.TierOrder());
    }

    /// <summary>
    /// 1. Everything under its minimum (crystals included), the furthest under (% of minimum) first.
    /// 2. Other items between minimum and target, by the chosen rule.
    /// 3. Crystals between minimum and target: your tier order (shards / crystals / clusters), then
    ///    the lowest % of target first within a tier.
    /// 4. Everything stocked.
    /// </summary>
    public static List<StockNeed> Order(List<StockNeed> list, int afterMinMode, List<int>? tierOrder = null)
    {
        tierOrder ??= new List<int> { 0, 1, 2 };
        int TierRank(StockNeed n) { var i = tierOrder.IndexOf(Items.CrystalTier(n.ItemId)); return i < 0 ? 9 : i; }
        double Fill(StockNeed n) => n.Target == 0 ? 1 : n.Total / (double)n.Target;

        var below = list.Where(n => n.BelowMin).OrderByDescending(n => (n.Min - n.Total) / (double)n.Min);
        var between = list.Where(n => !n.Crystal && !n.BelowMin && n.BelowTarget);
        between = afterMinMode == 1
            ? between.OrderBy(n => n.Target - n.Total)  // closest to target first
            : between.OrderBy(Fill);                    // spread evenly
        var crystals = list.Where(n => n.Crystal && !n.BelowMin && n.BelowTarget).OrderBy(TierRank).ThenBy(Fill);
        var done = list.Where(n => !n.BelowMin && !n.BelowTarget).OrderBy(n => n.Crystal).ThenBy(n => n.Name);
        return below.Concat(between).Concat(crystals).Concat(done).ToList();
    }
}
