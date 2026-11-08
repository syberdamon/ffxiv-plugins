using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;

namespace MaterialStash;

/// <summary>What one retainer held when last seen (opened by you, by a stash/scan run, or read from Allagan Tools).</summary>
[Serializable]
public class RetainerSnap
{
    public string Name { get; set; } = "";
    /// <summary>Item id (HQ = id + 1,000,000) -> total quantity, in the retainer's inventory pages.</summary>
    public Dictionary<uint, int> Items { get; set; } = new();
    /// <summary>Item id -> number of stacks (slots) it takes.</summary>
    public Dictionary<uint, int> Stacks { get; set; } = new();
    /// <summary>Item id -> how many have spiritbond (can't be sold on the market).</summary>
    public Dictionary<uint, int> Bound { get; set; } = new();
    /// <summary>Spiritbond was read (opened in game since 0.7.1); Allagan Tools records don't carry it.</summary>
    public bool BoundKnown { get; set; }
    /// <summary>Slots in use out of 175; -1 = unknown (only seen through Allagan Tools so far).</summary>
    public int UsedSlots { get; set; } = -1;
    /// <summary>Crystal item id (2-19) -> quantity.</summary>
    public Dictionary<uint, int> Crystals { get; set; } = new();
    public DateTime? Seen { get; set; }
    /// <summary>"opened" (read in game) or "allagan" (seeded from Allagan Tools).</summary>
    public string Source { get; set; } = "";
}

/// <summary>Per character: the retainers and where things go.</summary>
[Serializable]
public class CharacterData
{
    public string Name { get; set; } = "";
    /// <summary>Retainer ids in the game's list order (as the summoning bell shows them).</summary>
    public List<ulong> Order { get; set; } = new();
    public Dictionary<ulong, RetainerSnap> Retainers { get; set; } = new();
    /// <summary>Item category (ItemUICategory id) -> its home retainers, first choice first.</summary>
    public Dictionary<uint, List<ulong>> CategoryHomes { get; set; } = new();
    /// <summary>Where anything goes once its homes are full (or it has none), first choice first.</summary>
    public List<ulong> Overflow { get; set; } = new();
    /// <summary>Retainers never used for stashing (e.g. your materia retainer).</summary>
    public HashSet<ulong> Excluded { get; set; } = new();
    /// <summary>Retainers whose AutoRetainer ventures are never redirected for stock.</summary>
    public HashSet<ulong> NoRedirect { get; set; } = new();
    /// <summary>Ventures the game refused when we picked them (task id -> when): skipped for a week, then tried again.</summary>
    public Dictionary<uint, DateTime> BlockedVentures { get; set; } = new();
    /// <summary>Saddlebag contents when last open (item id incl. HQ offset -> quantity).</summary>
    public Dictionary<uint, int> Saddlebag { get; set; } = new();
    public DateTime? SaddlebagSeen { get; set; }
    /// <summary>Group name -> its home retainers.</summary>
    public Dictionary<string, List<ulong>> GroupHomes { get; set; } = new();
    /// <summary>One item (NQ id) -> its home retainers. Beats every other rule.</summary>
    public Dictionary<uint, List<ulong>> ItemPins { get; set; } = new();
    /// <summary>"Name contains ..." -> home retainers (e.g. Totem). After item pins, before categories.</summary>
    public List<NameRule> NameRules { get; set; } = new();
    /// <summary>Items a tidy run took out of a retainer and hasn't put back yet (NQ ids): deposited next run.</summary>
    public HashSet<uint> TidyCarry { get; set; } = new();
    /// <summary>Items fetched by a money venture, to sell (NQ ids): they stay in your bags, highlighted.</summary>
    public HashSet<uint> SellQueue { get; set; } = new();
    /// <summary>The retainer that lists them on the market (auto-sell).</summary>
    public ulong SellRetainer { get; set; }
    /// <summary>Clean-up: items you approved (id incl. HQ offset). Lapses when the item is gone.</summary>
    public HashSet<uint> CleanupApproved { get; set; } = new();
    /// <summary>Glamour dresser contents when last opened (item ids as the game stores them).</summary>
    public List<uint> Dresser { get; set; } = new();
    public DateTime? DresserSeen { get; set; }
    /// <summary>Items stored in the Armoire when it was last loaded (at an inn).</summary>
    public HashSet<uint> Armoire { get; set; } = new();
    public DateTime? ArmoireSeen { get; set; }
}

[Serializable]
public class NameRule
{
    public string Text { get; set; } = "";
    public List<ulong> Homes { get; set; } = new();
}

/// <summary>A bundle of the game's item categories sharing one home list (e.g. Housing).</summary>
[Serializable]
public class CategoryGroup
{
    public string Name { get; set; } = "";
    public HashSet<uint> Categories { get; set; } = new();
    /// <summary>Stash also takes this group's items from your bags (Materials: the material rules apply).</summary>
    public bool StashFromBags { get; set; }
    /// <summary>Tidy may move this group's items between retainers.</summary>
    public bool Tidy { get; set; } = true;
}

/// <summary>Keep at least Min of an item (bags + saddlebag + retainers); ventures refill it toward Target.</summary>
[Serializable]
public class StockItem
{
    public uint ItemId { get; set; }
    public int Min { get; set; }
    public int Target { get; set; }
}

/// <summary>Crystal stock for one tier (shards, crystals or clusters), or one element's override.</summary>
[Serializable]
public class CrystalStock
{
    /// <summary>Below this (bags + all retainers combined) the type competes with other short stock items.</summary>
    public int Min { get; set; }
    /// <summary>Filled up to this after everything else; 0 = no limit (until the retainers' crystal tabs are full).</summary>
    public int Target { get; set; }
}

/// <summary>
/// Keep enough materials to make Min / Target of a recipe's result (in items made, not crafts:
/// the recipe's yield is applied). Crafted ingredients are broken down to their own materials
/// unless listed in KeepAsIs.
/// </summary>
[Serializable]
public class StockRecipe
{
    public uint RecipeId { get; set; }
    public int Min { get; set; }
    public int Target { get; set; }
    public HashSet<uint> KeepAsIs { get; set; } = new();
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Category groups (seeded with defaults on first load; see EnsureGroups).</summary>
    public List<CategoryGroup> Groups { get; set; } = new();
    /// <summary>Never taken out of your bags by Stash or the saddlebag buttons (raid food, dark matter...). NQ ids.</summary>
    public HashSet<uint> KeepOnMe { get; set; } = new();
    /// <summary>After tidying a retainer, run the game's item sort on it (also merges part stacks).</summary>
    public bool TidySort { get; set; } = true;
    /// <summary>Laps around the retainers a tidy may take.</summary>
    public int TidyMaxLaps { get; set; } = 4;
    /// <summary>After AutoRetainer finishes, stash what ventures brought back (it lands in your bags).</summary>
    public bool StashAfterAutoRetainer { get; set; } = true;

    // ---- venture timer window
    public bool ShowVentureWindow { get; set; } = true;
    public bool VentureWindowAlways { get; set; } = false;

    public Dictionary<ulong, CharacterData> Characters { get; set; } = new();

    // ---- what counts
    public bool IncludeGathered { get; set; } = true;
    public bool IncludeMobDrops { get; set; } = true;
    public bool IncludeCrystals { get; set; } = true;
    /// <summary>Always stash these (item ids, NQ id), whatever the rules say.</summary>
    public HashSet<uint> Include { get; set; } = new();
    /// <summary>Never stash these (item ids, NQ id).</summary>
    public HashSet<uint> Exclude { get; set; } = new();
    /// <summary>Put an item with retainers that already hold it before its category homes.</summary>
    public bool KeepTogether { get; set; } = true;

    // ---- retainer room
    /// <summary>Leave this many inventory slots free on every retainer.</summary>
    public int ReserveSlots { get; set; } = 0;

    // ---- crystals: fill retainers in list order, keep some on you
    public int CrystalKeep { get; set; } = 0;
    /// <summary>Per crystal item id: keep this many instead of CrystalKeep.</summary>
    public Dictionary<uint, int> CrystalKeepPer { get; set; } = new();
    /// <summary>Below the keep amount, take crystals back from the last retainer holding them.</summary>
    public bool CrystalWithdrawToKeep { get; set; } = true;

    // ---- stock (refilled through AutoRetainer ventures)
    public List<StockItem> StockItems { get; set; } = new();
    public List<StockRecipe> StockRecipes { get; set; } = new();
    /// <summary>When AutoRetainer sends a retainer out, send it for the most-needed stock item it can bring back.</summary>
    public bool RedirectVentures { get; set; } = true;
    /// <summary>Once every minimum is met: 0 = spread evenly (lowest % of target first), 1 = closest to its target first.</summary>
    public int AfterMinimumMode { get; set; } = 0;
    /// <summary>Venture for crystals at all (shards, crystals, clusters).</summary>
    public bool CrystalStockOn { get; set; } = false;
    /// <summary>Tier order once their minimums are met: 0 = shards, 1 = crystals, 2 = clusters.</summary>
    /// <remarks>Starts empty (the settings loader would append to a pre-filled list); see TierOrder().</remarks>
    public List<int> CrystalTierOrder { get; set; } = new();
    /// <summary>Per tier (0 shards, 1 crystals, 2 clusters): minimum and target for each element.</summary>
    public List<CrystalStock> CrystalTiers { get; set; } = new();
    /// <summary>One element of one tier with its own numbers (crystal item id -> numbers).</summary>
    public Dictionary<uint, CrystalStock> CrystalStockPer { get; set; } = new();
    /// <summary>Print each redirect to chat.</summary>
    public bool AnnounceRedirects { get; set; } = true;

    // ---- money ventures: when nothing on the stock list fits a retainer
    public bool MoneyVentures { get; set; } = true;
    /// <summary>The item must sell at least this many ventures' worth a day on your home world.</summary>
    public float DemandFactor { get; set; } = 3f;
    /// <summary>Count what other retainers are already fetching, and what you hold to sell, against that demand.</summary>
    public bool DemandCombined { get; set; } = true;
    /// <summary>A money venture must be worth at least this (after the 5% market tax).</summary>
    public int MinVentureValue { get; set; } = 200;
    /// <summary>Prices older than this are refreshed (minutes).</summary>
    public int PriceRefreshMinutes { get; set; } = 60;
    // ---- to-sell alert
    public bool SellSound { get; set; } = true;
    /// <summary>Chat sound effect number (the &lt;se.N&gt; sounds, 1-16).</summary>
    public int SellSoundId { get; set; } = 6;
    public bool SellHighlight { get; set; } = true;
    // ---- auto-sell
    public bool AutoSell { get; set; } = false;
    /// <summary>After a Stash run, go to the selling retainer and list what's queued.</summary>
    public bool SellAfterStash { get; set; } = true;
    /// <summary>List at the lowest home-world listing minus this.</summary>
    public int Undercut { get; set; } = 1;
    /// <summary>Most items in one listing.</summary>
    public int MaxPerListing { get; set; } = 99;

    // ---- clean-up
    /// <summary>A stack is listed on the market only if it fetches at least this (after tax).</summary>
    public int CleanupMinSell { get; set; } = 1000;
    /// <summary>Stacks worth more than this get their own look instead of being approved with their group.</summary>
    public int CleanupReview { get; set; } = 10000;
    /// <summary>Leave the Materials group, materia, crystals and stock-list items alone.</summary>
    public bool CleanupSkipMaterials { get; set; } = true;
    /// <summary>Desynthesize instead of vendoring when an item can be desynthesized.</summary>
    public bool CleanupPreferDesynth { get; set; } = false;
    /// <summary>After a clean-up run, use the unlearned items it brought to your bags.</summary>
    public bool CleanupAutoUse { get; set; } = true;
    /// <summary>After a clean-up run, go to an inn (Lifestream) and put "keep the look" items in the Armoire / glamour dresser.</summary>
    public bool CleanupStoreLooks { get; set; } = true;
    public bool CleanupAutoDesynth { get; set; } = true;
    /// <summary>Your verdict per item (NQ id), instead of the suggested one.</summary>
    public Dictionary<uint, Verdict> CleanupVerdicts { get; set; } = new();

    // ---- other plugins / UI
    public bool UseAllaganTools { get; set; } = true;
    public bool ShowBellButtons { get; set; } = true;
    public bool ShowSaddlebagButtons { get; set; } = true;
    /// <summary>Nudge for the buttons above the retainer list (pixels).</summary>
    public float BellOffsetX { get; set; } = 0;
    public float BellOffsetY { get; set; } = 0;
    /// <summary>Pause between moves (seconds).</summary>
    public float StepDelay { get; set; } = 0.4f;

    /// <summary>The tier order, repaired to exactly shards/crystals/clusters once each.</summary>
    public List<int> TierOrder()
    {
        if (CrystalTierOrder.Count != 3 || CrystalTierOrder.Distinct().Count() != 3 || CrystalTierOrder.Any(t => t is < 0 or > 2))
            CrystalTierOrder = new List<int> { 0, 1, 2 };
        while (CrystalTiers.Count < 3) CrystalTiers.Add(new CrystalStock());
        if (CrystalTiers.Count > 3) CrystalTiers.RemoveRange(3, CrystalTiers.Count - 3);
        return CrystalTierOrder;
    }

    public CrystalStock CrystalStockFor(uint crystalId)
    {
        if (CrystalStockPer.TryGetValue(crystalId, out var own)) return own;
        TierOrder();
        return CrystalTiers[Items.CrystalTier(crystalId)];
    }

    public int KeepOf(uint crystalId) => CrystalKeepPer.TryGetValue(crystalId, out var k) ? k : CrystalKeep;

    public const string MaterialsGroup = "Materials";

    /// <summary>Default groups from the game's own category ordering (OrderMajor); one-time, then yours to edit.</summary>
    public void EnsureGroups()
    {
        if (Version < 2)
        {
            // 0.1-0.3 "never stash" list becomes "keep on me"
            foreach (var id in Exclude) KeepOnMe.Add(id);
            Exclude.Clear();
        }
        if (Groups.Count == 0)
        {
            var housing = new HashSet<uint> { 57, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 82, 95 };
            var collect = new HashSet<uint> { 81, 85, 86, 94, 112 };
            var byMajor = new Dictionary<int, HashSet<uint>>();
            foreach (var cat in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ItemUICategory>())
            {
                if (cat.RowId == 0 || cat.Name.ExtractText().Length == 0 || cat.RowId == 59 /* crystals: own rules */) continue;
                if (housing.Contains(cat.RowId) || collect.Contains(cat.RowId) || cat.RowId == 58) continue;
                int major = cat.OrderMajor;
                if (!byMajor.TryGetValue(major, out var set)) byMajor[major] = set = new();
                set.Add(cat.RowId);
            }
            HashSet<uint> Of(params int[] majors) => majors.SelectMany(m => byMajor.GetValueOrDefault(m) ?? new()).ToHashSet();
            Groups = new List<CategoryGroup>
            {
                new() { Name = MaterialsGroup, Categories = Of(6), StashFromBags = true },
                new() { Name = "Housing", Categories = housing },
                new() { Name = "Armor", Categories = Of(3) },
                new() { Name = "Accessories", Categories = Of(4) },
                new() { Name = "Weapons & tools", Categories = Of(1, 2) },
                new() { Name = "Food & potions", Categories = Of(5) },
                new() { Name = "Collectibles", Categories = collect },
                new() { Name = "Materia", Categories = new() { 58 }, Tidy = false },
                new() { Name = "Other", Categories = Of(7) },
            };
        }
        Version = 2;
    }

    public CategoryGroup? GroupOf(uint categoryId) => Groups.FirstOrDefault(g => g.Categories.Contains(categoryId));

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
