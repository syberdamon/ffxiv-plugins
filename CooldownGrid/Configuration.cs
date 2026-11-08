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

    /// <summary>Clicking an icon casts it on the tank who's tanking (healers' "Tank" group).</summary>
    public bool CastOnTank { get; set; } = false;
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
    public static readonly Guid TankId = new("6f1c1d0a-0000-4000-8000-000000000005");

    public int Version { get; set; } = 3;

    public List<CooldownGroup> Groups { get; set; } = new();

    /// <summary>ClassJob row id -> abilities placed in groups.</summary>
    public Dictionary<uint, List<Assignment>> Jobs { get; set; } = new();

    public bool ShowCooldownNumbers { get; set; } = true;
    public bool ShowTooltips { get; set; } = true;
    public bool TintUnusable { get; set; } = true;

    // ---- Floating "Plugins" button (shown during cutscenes too)
    public bool ShowPluginsButton { get; set; } = true;
    public bool PluginsButtonCutscenesOnly { get; set; } = false;
    public bool PluginsButtonLocked { get; set; } = false;

    // ---- Alerts: icons pulse when they're due.
    public bool PulseBeforeBusters { get; set; } = true;     // icons in a "cast on the tank" group
    public bool PulseBeforeRaidwides { get; set; } = true;   // icons in Defensive (group)
    public bool PulseMissingUpkeep { get; set; } = true;     // Regen / Aspected Benefic / Kardia not on the tank
    public float AlertLeadSeconds { get; set; } = 5f;

    /// <summary>Make sure the three default groups exist (first run, or a damaged config).</summary>
    public void EnsureDefaults()
    {
        AddDefault(DefensiveSelfId, "Defensive (self)");
        AddDefault(DefensiveGroupId, "Defensive (group)");
        AddDefault(SupportId, "Support");
        AddDefault(OtSentMitsId, "OT Sent Mits", castOnCoTank: true);
        AddDefault(TankId, "Tank", castOnTank: true);
    }

    private void AddDefault(Guid id, string name, bool castOnCoTank = false, bool castOnTank = false)
    {
        if (Groups.Any(g => g.Id == id)) return;
        Groups.Add(new CooldownGroup { Id = id, Name = name, IsDefault = true, CastOnCoTank = castOnCoTank, CastOnTank = castOnTank });
    }

    /// <summary>v2: Shirk moves from Support to OT Sent Mits (it always targets the other tank).</summary>
    public void Migrate()
    {
        if (Version < 2) MigrateShirk();
        if (Version < 3)
        {
            // v3: healers get a starter layout. Replace a healer job's list only if it's still the
            // old untouched default (just Sprint + Limit Break).
            foreach (var job in Presets.HealerJobs)
                if (Jobs.TryGetValue(job, out var list) && list.All(a => a.GroupId == SupportId) &&
                    list.Select(a => a.ActionId).OrderBy(x => x).SequenceEqual(Presets.EveryoneIds.OrderBy(x => x)))
                    Jobs[job] = Presets.For(job);
            Version = 3;
        }
    }

    private void MigrateShirk()
    {
        foreach (var list in Jobs.Values)
        {
            var shirks = list.Where(a => a.ActionId == Presets.Shirk).ToList();
            if (shirks.Count == 0) continue;
            var at = list.IndexOf(shirks[0]);
            list.RemoveAll(a => a.ActionId == Presets.Shirk);
            list.Insert(Math.Min(at, list.Count), new Assignment { ActionId = Presets.Shirk, GroupId = OtSentMitsId });
        }
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
