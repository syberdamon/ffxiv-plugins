using System;
using System.Collections.Generic;
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
    /// <summary>Saddlebag contents when last open (item id incl. HQ offset -> quantity).</summary>
    public Dictionary<uint, int> Saddlebag { get; set; } = new();
    public DateTime? SaddlebagSeen { get; set; }
}

/// <summary>Keep at least Min of an item (bags + saddlebag + retainers); ventures refill it toward Target.</summary>
[Serializable]
public class StockItem
{
    public uint ItemId { get; set; }
    public int Min { get; set; }
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
    /// <summary>Print each redirect to chat.</summary>
    public bool AnnounceRedirects { get; set; } = true;

    // ---- other plugins / UI
    public bool UseAllaganTools { get; set; } = true;
    public bool ShowBellButtons { get; set; } = true;
    public bool ShowSaddlebagButtons { get; set; } = true;
    /// <summary>Nudge for the buttons above the retainer list (pixels).</summary>
    public float BellOffsetX { get; set; } = 0;
    public float BellOffsetY { get; set; } = 0;
    /// <summary>Pause between moves (seconds).</summary>
    public float StepDelay { get; set; } = 0.4f;

    public int KeepOf(uint crystalId) => CrystalKeepPer.TryGetValue(crystalId, out var k) ? k : CrystalKeep;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
