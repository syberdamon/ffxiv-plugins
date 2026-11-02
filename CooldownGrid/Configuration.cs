using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;

namespace CooldownGrid;

/// <summary>One window of icons.</summary>
[Serializable]
public class CooldownGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New group";
    public int Columns { get; set; } = 4;
    public int IconSize { get; set; } = 40;
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; } = false;
    public bool ShowTitleBar { get; set; } = true;
    public bool ShowBackground { get; set; } = true;

    /// <summary>The default groups can be renamed but not deleted.</summary>
    public bool IsDefault { get; set; } = false;

    /// <summary>Clicking an icon casts it on the other tank in the party (OT Sent Mits).</summary>
    public bool CastOnCoTank { get; set; } = false;
}

/// <summary>An ability placed in a group, for one job. List order is display order.</summary>
[Serializable]
public class Assignment
{
    public uint ActionId { get; set; }
    public Guid GroupId { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public static readonly Guid DefensiveSelfId = new("6f1c1d0a-0000-4000-8000-000000000001");
    public static readonly Guid DefensiveGroupId = new("6f1c1d0a-0000-4000-8000-000000000002");
    public static readonly Guid SupportId = new("6f1c1d0a-0000-4000-8000-000000000003");
    public static readonly Guid OtSentMitsId = new("6f1c1d0a-0000-4000-8000-000000000004");

    public int Version { get; set; } = 1;

    public List<CooldownGroup> Groups { get; set; } = new();

    /// <summary>ClassJob row id -> abilities placed in groups.</summary>
    public Dictionary<uint, List<Assignment>> Jobs { get; set; } = new();

    public bool ShowCooldownNumbers { get; set; } = true;
    public bool ShowTooltips { get; set; } = true;
    public bool TintUnusable { get; set; } = true;

    /// <summary>Make sure the three default groups exist (first run, or a damaged config).</summary>
    public void EnsureDefaults()
    {
        AddDefault(DefensiveSelfId, "Defensive (self)");
        AddDefault(DefensiveGroupId, "Defensive (group)");
        AddDefault(SupportId, "Support");
        AddDefault(OtSentMitsId, "OT Sent Mits", castOnCoTank: true);
    }

    private void AddDefault(Guid id, string name, bool castOnCoTank = false)
    {
        if (Groups.Any(g => g.Id == id)) return;
        Groups.Add(new CooldownGroup { Id = id, Name = name, IsDefault = true, CastOnCoTank = castOnCoTank });
    }

    public List<Assignment> ForJob(uint jobId)
    {
        if (!Jobs.TryGetValue(jobId, out var list))
        {
            list = Presets.For(jobId);
            Jobs[jobId] = list;
            Save();
        }
        return list;
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
