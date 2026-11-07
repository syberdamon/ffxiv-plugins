using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace RetainerGear;

/// <summary>What a retainer had on the last time you opened them.</summary>
[Serializable]
public class RetainerRecord
{
    public ulong Id { get; set; }
    public string Name { get; set; } = "";
    public uint ClassJob { get; set; }
    public int Level { get; set; }
    /// <summary>Equip slot index (0 main hand ... 12 ring) -> item id (HQ ids stored without the +1,000,000).</summary>
    public Dictionary<int, uint> Equipped { get; set; } = new();
    /// <summary>Item ids in the retainer's inventory pages (spare gear counts as "you have it").</summary>
    public List<uint> Inventory { get; set; } = new();
    public DateTime? GearSeen { get; set; }
    public DateTime? InventorySeen { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Content id of the character -> their retainers.</summary>
    public Dictionary<ulong, List<RetainerRecord>> Characters { get; set; } = new();

    /// <summary>Uncapped currencies whose gear counts (English item names; Mathematics and Poetics by default).</summary>
    public List<string> Currencies { get; set; } = new() { "Allagan Tomestone of Mathematics", "Allagan Tomestone of Poetics" };

    public bool UseCrafted { get; set; } = true;
    public bool UseDungeon { get; set; } = true;
    public bool UseTomestones { get; set; } = true;
    public bool ShowPrices { get; set; } = true;

    /// <summary>Gear you looted in a dungeon (learned as you play): item id -> territory id.</summary>
    public Dictionary<uint, uint> LearnedDrops { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
