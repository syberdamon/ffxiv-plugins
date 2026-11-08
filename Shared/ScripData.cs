using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Syberdamon.Shared;

/// <summary>What the Collectable Appraiser pays for one collectable.</summary>
public sealed record CollectableReward(
    uint ItemId, string Name, uint CurrencyItemId,
    int Low, int Mid, int High,
    int LowCollectability, int MidCollectability, int HighCollectability,
    int LevelMin, int LevelMax, uint RequiredQuest, int JobTab);

/// <summary>One item in the scrip exchange: what it costs and in which scrip.</summary>
public sealed record ScripShopEntry(uint ItemId, string Name, uint CurrencyItemId, int Cost);

/// <summary>A crafting recipe whose result the appraiser takes, with what it pays.</summary>
public sealed record CollectableRecipe(
    uint RecipeId, uint ResultId, string Name, int CraftType, string Job,
    int RecipeLevel, uint SecretBook, CollectableReward Reward);

/// <summary>
/// Scrip data shared by Material Stash (planning) and Turn-In Manager (turn-ins and buying).
/// Read from the game's sheets: CollectablesShopItem (+ Refine, RewardScrip) for what collectables
/// pay, InclusionShop -> InclusionShopCategory -> InclusionShopSeries -> SpecialShop for what the
/// scrip exchange sells. Sheet layout learned from GatherBuddy Reborn and TheCollector (see
/// THIRD_PARTY_NOTICES); the code is our own.
/// </summary>
public static unsafe class ScripData
{
    /// <summary>A scrip wallet holds at most this many of each kind.</summary>
    public const int ScripCap = 4000;

    public const uint PurpleCrafters = 33913, PurpleGatherers = 33914, OrangeCrafters = 41784, OrangeGatherers = 41785;

    public static readonly Dictionary<uint, string> ScripNames = new()
    {
        [PurpleCrafters] = "Purple Crafters' Scrip",
        [PurpleGatherers] = "Purple Gatherers' Scrip",
        [OrangeCrafters] = "Orange Crafters' Scrip",
        [OrangeGatherers] = "Orange Gatherers' Scrip",
    };

    public static bool IsCrafterScrip(uint currencyItemId) => currencyItemId is PurpleCrafters or OrangeCrafters;

    // The sheets name scrips by a small "special currency" number. [Assumption] 2/4/6/7 are the
    // four current scrips (as TheCollector maps them); the game's own lookup is tried first for rewards.
    private static readonly Dictionary<uint, uint> Aliases = new()
    {
        [2] = PurpleCrafters, [4] = PurpleGatherers, [6] = OrangeCrafters, [7] = OrangeGatherers,
    };

    private static Dictionary<uint, CollectableReward>? rewards;
    private static Dictionary<uint, ScripShopEntry>? shop;
    private static Dictionary<uint, int>? gatherJobs;

    // ------------------------------------------------------------------ currencies

    public static uint RewardCurrency(uint raw)
    {
        if (raw >= 1000) return raw;
        var cm = CurrencyManager.Instance();
        var id = cm == null ? 0u : cm->GetItemIdBySpecialId((byte)raw);
        if (ScripNames.ContainsKey(id)) return id;
        return Aliases.TryGetValue(raw, out var a) ? a : id;
    }

    private static uint CostCurrency(uint raw)
    {
        if (ScripNames.ContainsKey(raw)) return raw;
        return Aliases.TryGetValue(raw, out var a) ? a : 0;
    }

    /// <summary>Scrips of this kind you hold now.</summary>
    public static int Scrips(uint currencyItemId)
    {
        var cm = CurrencyManager.Instance();
        if (cm != null)
        {
            var n = (int)cm->GetItemCount(currencyItemId);
            if (n > 0) return n;
        }
        var im = InventoryManager.Instance();
        return im == null ? 0 : im->GetInventoryItemCount(currencyItemId);
    }

    // ------------------------------------------------------------------ collectables

    /// <summary>Every collectable the appraiser takes for scrips, by item id.</summary>
    public static IReadOnlyDictionary<uint, CollectableReward> Rewards(IDataManager dm)
    {
        if (rewards != null) return rewards;
        var map = new Dictionary<uint, CollectableReward>();
        try
        {
            var jobs = CraftJobs(dm);
            var gather = GatherJobs(dm);
            foreach (var row in dm.GetSubrowExcelSheet<CollectablesShopItem>())
            foreach (var sub in row)
            {
                var itemId = sub.Item.RowId;
                if (itemId == 0 || map.ContainsKey(itemId)) continue;
                if (sub.CollectablesShopRewardScrip.ValueNullable is not { } rs || rs.Currency == 0) continue;
                var currency = RewardCurrency(rs.Currency);
                if (!ScripNames.ContainsKey(currency)) continue;
                var rf = sub.CollectablesShopRefine.ValueNullable;
                var name = dm.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"#{itemId}";
                var tab = jobs.TryGetValue(itemId, out var j) ? j : gather.TryGetValue(itemId, out var g) ? g : -1;
                map[itemId] = new CollectableReward(itemId, name, currency,
                    rs.LowReward, rs.MidReward, rs.HighReward,
                    rf?.LowCollectability ?? 0, rf?.MidCollectability ?? 0, rf?.HighCollectability ?? 0,
                    sub.LevelMin, sub.LevelMax, sub.RequiredQuest.RowId, tab);
            }
        }
        catch (Exception ex) { Console.WriteLine($"ScripData: reading collectables failed: {ex}"); }
        return rewards = map;
    }

    /// <summary>Crafted item -> its crafting job (0 = Carpenter ... 7 = Culinarian), the appraiser's job tab.</summary>
    private static Dictionary<uint, int> CraftJobs(IDataManager dm)
    {
        var map = new Dictionary<uint, int>();
        foreach (var r in dm.GetExcelSheet<Recipe>())
            if (r.ItemResult.RowId != 0 && !map.ContainsKey(r.ItemResult.RowId))
                map[r.ItemResult.RowId] = (int)r.CraftType.RowId;
        return map;
    }

    /// <summary>Gathered item -> job tab (8 Miner, 9 Botanist, 10 Fisher).</summary>
    private static Dictionary<uint, int> GatherJobs(IDataManager dm)
    {
        if (gatherJobs != null) return gatherJobs;
        var map = new Dictionary<uint, int>();
        try
        {
            foreach (var f in dm.GetExcelSheet<FishParameter>())
                if (f.Item.RowId != 0) map[f.Item.RowId] = 10;
            var byGatherRow = dm.GetExcelSheet<GatheringItem>()
                .Where(g => g.RowId != 0 && g.Item.RowId != 0)
                .ToDictionary(g => g.RowId, g => g.Item.RowId);
            foreach (var b in dm.GetExcelSheet<GatheringPointBase>())
            {
                var job = b.GatheringType.RowId switch { 0 or 1 or 6 => 8, 2 or 3 or 5 => 9, 4 or 7 => 10, _ => -1 };
                if (job < 0) continue;
                foreach (var gi in b.Item)
                    if (gi.RowId != 0 && byGatherRow.TryGetValue(gi.RowId, out var item)) map[item] = job;
            }
        }
        catch (Exception ex) { Console.WriteLine($"ScripData: reading gathering jobs failed: {ex}"); }
        return gatherJobs = map;
    }

    /// <summary>Collectables in your bags (each its own slot) the appraiser takes.</summary>
    public static List<(InventoryType Type, int Slot, uint ItemId, int Collectability)> CollectablesInBags(IDataManager dm, uint? currency = null)
    {
        var list = new List<(InventoryType, int, uint, int)>();
        var im = InventoryManager.Instance();
        if (im == null) return list;
        var rw = Rewards(dm);
        foreach (var t in new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 })
        {
            var c = im->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0 || !s->Flags.HasFlag(InventoryItem.ItemFlags.Collectable)) continue;
                if (!rw.TryGetValue(s->ItemId, out var r)) continue;
                if (currency is { } cur && r.CurrencyItemId != cur) continue;
                list.Add((t, i, s->ItemId, s->SpiritbondOrCollectability));
            }
        }
        return list;
    }

    /// <summary>What one collectable pays at the collectability it has (0 if below the lowest tier).</summary>
    public static int Pays(CollectableReward r, int collectability) =>
        r.HighCollectability > 0 && collectability >= r.HighCollectability ? r.High
        : r.MidCollectability > 0 && collectability >= r.MidCollectability ? r.Mid
        : collectability >= r.LowCollectability ? r.Low : 0;

    // ------------------------------------------------------------------ scrip exchange

    /// <summary>Everything the scrip exchange sells for one of the four scrips, by item id.</summary>
    public static IReadOnlyDictionary<uint, ScripShopEntry> Shop(IDataManager dm)
    {
        if (shop != null) return shop;
        var map = new Dictionary<uint, ScripShopEntry>();
        try
        {
            var items = dm.GetExcelSheet<Item>();
            var series = dm.GetSubrowExcelSheet<InclusionShopSeries>();
            var specials = new HashSet<uint>();
            foreach (var inc in dm.GetExcelSheet<InclusionShop>())
            foreach (var cat in inc.Category)
            {
                if (cat.RowId == 0 || cat.ValueNullable is not { } c || c.InclusionShopSeries.RowId == 0) continue;
                if (!series.TryGetRow(c.InclusionShopSeries.RowId, out var subs)) continue;
                foreach (var s in subs)
                    if (s.SpecialShop.RowId != 0) specials.Add(s.SpecialShop.RowId);
            }
            var sheet = dm.GetExcelSheet<SpecialShop>();
            foreach (var id in specials)
            {
                if (!sheet.TryGetRow(id, out var sp)) continue;
                foreach (var entry in sp.Item)
                {
                    uint currency = 0; var cost = 0;
                    foreach (var c in entry.ItemCosts)
                    {
                        if (c.ItemCost.RowId == 0 || c.CurrencyCost == 0) continue;
                        currency = CostCurrency(c.ItemCost.RowId);
                        cost = (int)c.CurrencyCost;
                        break;
                    }
                    if (currency == 0 || cost <= 0) continue;
                    foreach (var rcv in entry.ReceiveItems)
                    {
                        var itemId = rcv.Item.RowId;
                        if (itemId == 0 || map.ContainsKey(itemId)) continue;
                        var name = items.GetRowOrDefault(itemId)?.Name.ExtractText() ?? "";
                        if (name.Length == 0) continue;
                        map[itemId] = new ScripShopEntry(itemId, name, currency, cost);
                    }
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"ScripData: reading the scrip exchange failed: {ex}"); }
        return shop = map;
    }

    // ------------------------------------------------------------------ recipes

    private static List<CollectableRecipe>? recipes;

    /// <summary>Every crafting recipe that makes a collectable the appraiser pays scrips for.</summary>
    public static IReadOnlyList<CollectableRecipe> Recipes(IDataManager dm)
    {
        if (recipes != null) return recipes;
        var list = new List<CollectableRecipe>();
        try
        {
            var rw = Rewards(dm);
            var jobs = dm.GetExcelSheet<ClassJob>();
            foreach (var r in dm.GetExcelSheet<Recipe>())
            {
                if (r.ItemResult.RowId == 0 || !rw.TryGetValue(r.ItemResult.RowId, out var reward)) continue;
                var craftType = (int)r.CraftType.RowId;
                var job = jobs.GetRowOrDefault((uint)(craftType + 8))?.Abbreviation.ExtractText() ?? $"#{craftType}";
                var level = r.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 0;
                list.Add(new CollectableRecipe(r.RowId, r.ItemResult.RowId, reward.Name, craftType, job, level, r.SecretRecipeBook.RowId, reward));
            }
        }
        catch (Exception ex) { Console.WriteLine($"ScripData: reading recipes failed: {ex}"); }
        return recipes = list;
    }

    /// <summary>Your level on a crafting job (craft type 0-7), 0 if unknown.</summary>
    public static int CraftLevel(IDataManager dm, int craftType)
    {
        var ps = PlayerState.Instance();
        if (ps == null) return 0;
        var cj = dm.GetExcelSheet<ClassJob>().GetRowOrDefault((uint)(craftType + 8));
        if (cj == null || cj.Value.ExpArrayIndex < 0) return 0;
        return ps->ClassJobLevels[cj.Value.ExpArrayIndex];
    }

    /// <summary>Why you can't make and turn in this recipe now, or null if you can.</summary>
    public static string? CantUse(IDataManager dm, CollectableRecipe r)
    {
        var lv = CraftLevel(dm, r.CraftType);
        if (lv < r.RecipeLevel) return $"{r.Job} {lv} (recipe needs {r.RecipeLevel})";
        if (r.Reward.LevelMin > 0 && lv < r.Reward.LevelMin) return $"{r.Job} {lv} (appraiser takes it from {r.Reward.LevelMin})";
        if (r.Reward.LevelMax > 0 && lv > r.Reward.LevelMax) return $"{r.Job} {lv} (appraiser takes it up to {r.Reward.LevelMax})";
        var ps = PlayerState.Instance();
        if (r.SecretBook != 0 && ps != null && !ps->IsSecretRecipeBookUnlocked(r.SecretBook)) return "master recipe book not learned";
        if (r.Reward.RequiredQuest != 0 && !QuestManager.IsQuestComplete(r.Reward.RequiredQuest)) return "turn-in quest not done";
        return null;
    }

    /// <summary>Recipes paying this scrip that you can use, best scrips per craft first.</summary>
    public static List<CollectableRecipe> Best(IDataManager dm, uint currencyItemId) =>
        Recipes(dm).Where(r => r.Reward.CurrencyItemId == currencyItemId && CantUse(dm, r) == null)
            .OrderByDescending(r => r.Reward.High).ThenByDescending(r => r.RecipeLevel).ToList();
}
