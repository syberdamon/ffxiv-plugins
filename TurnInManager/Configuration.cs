using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace TurnInManager;

/// <summary>Buy this item with seals until you hold the target amount.</summary>
[Serializable]
public class PurchaseRule
{
    public bool Enabled { get; set; } = true;
    public uint ItemId { get; set; }
    public string Name { get; set; } = "";
    public int Target { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // ---- what may be turned in (guardrails)
    public bool SkipGearsetItems { get; set; } = true;       // never turn in anything in a gearset
    public int MaxItemLevel { get; set; } = 600;             // [Estimate] set to just below your current gear
    public bool AllowGreen { get; set; } = true;             // rarity 2
    public bool AllowBlue { get; set; } = true;              // rarity 3
    public bool AllowNq { get; set; } = true;
    public bool AllowHq { get; set; } = true;
    public bool IncludeArmoury { get; set; } = true;
    public HashSet<uint> NeverTurnIn { get; set; } = new();  // item ids you've protected

    // ---- materia
    public bool RetrieveMateria { get; set; } = true;
    /// <summary>If retrieval is off, skip gear with materia (otherwise the materia is lost).</summary>
    public bool SkipItemsWithMateria { get; set; } = true;

    // ---- seals
    /// <summary>Buy before a delivery would push seals over the cap.</summary>
    public bool BuyBeforeCap { get; set; } = true;
    /// <summary>Also spend down at the end of a run (to this reserve).</summary>
    public bool SpendAtEnd { get; set; } = false;
    public int SealReserve { get; set; } = 0;
    // Replace, don't append: otherwise every load adds the saved rows to the default Venture row.
    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)]
    public List<PurchaseRule> Purchases { get; set; } = new()
    {
        new PurchaseRule { ItemId = 21072, Name = "Venture", Target = 65000 },
    };

    // ---- travel
    public bool UseLifestream { get; set; } = true;

    // ---- scrips (collectables turn-in + scrip exchange)
    /// <summary>Where scrip runs go: territory, its aetheryte, and an aethernet shard near the NPCs (0 = none).
    /// Default Solution Nine via the Nexus Arcade shard (route from TheCollector).</summary>
    public uint HubTerritory { get; set; } = 1186;
    public uint HubAetheryte { get; set; } = 217;
    public uint HubAethernet { get; set; } = 235;
    /// <summary>[Assumption] the NPCs' English names in most hubs; a name containing "Appraiser" / "Scrip" nearby is used if these aren't found.</summary>
    public string AppraiserName { get; set; } = "Collectable Appraiser";
    public string ExchangeName { get; set; } = "Scrip Exchange";
    /// <summary>Pause between clicks in the collectables / scrip windows (seconds).</summary>
    public float ScripStepDelay { get; set; } = 0.6f;
    /// <summary>Where an item was found in the scrip exchange: item id -> [page, sub-page].</summary>
    public Dictionary<uint, int[]> ShopSpots { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
