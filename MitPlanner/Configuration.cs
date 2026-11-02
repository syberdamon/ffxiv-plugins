using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace MitPlanner;

/// <summary>"Use this cooldown for that mechanic."</summary>
[Serializable]
public class PlanEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Which mechanic, as label + occurrence: "Stamp#2".</summary>
    public string EventKey { get; set; } = "";

    /// <summary>Base action ID (upgrades like Vengeance -> Damnation resolve automatically).</summary>
    public uint ActionId { get; set; }

    /// <summary>Press this many seconds before the mechanic lands.</summary>
    public float LeadSeconds { get; set; } = 3f;

    /// <summary>Let the plugin press it at the planned time (also needs the global switch).</summary>
    public bool AutoUse { get; set; } = false;

    /// <summary>Only call it out / press it depending on who the mechanic targets.</summary>
    public TargetCondition Condition { get; set; } = TargetCondition.Always;
}

public enum TargetCondition
{
    Always = 0,
    IfTargeted = 1,
    IfNotTargeted = 2,
}

public enum AbilitySort
{
    Cooldown = 0,
    Name = 1,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>"territoryId:classJobId" -> plan.</summary>
    public Dictionary<string, List<PlanEntry>> Plans { get; set; } = new();

    /// <summary>"territoryId:label" -> mechanic type, overriding the bundled tags.</summary>
    public Dictionary<string, MechanicType> TagOverrides { get; set; } = new();

    /// <summary>"territoryId:eventKey" -> tank swap after this mechanic (overrides bundled data).</summary>
    public Dictionary<string, bool> SwapOverrides { get; set; } = new();

    // Display
    public bool ShowTimeline { get; set; } = true;
    public int UpcomingCount { get; set; } = 5;
    public bool ShowOnlyTagged { get; set; } = false;
    public bool LockWindows { get; set; } = false;
    public float CalloutScale { get; set; } = 1.8f;

    /// <summary>Seconds before the press time that the callout starts counting down.</summary>
    public float CalloutHeadsUp { get; set; } = 3f;

    public bool ChatCallouts { get; set; } = false;

    /// <summary>Master switch for the plan: callouts and auto-use. The timeline still shows when off.</summary>
    public bool PlanEnabled { get; set; } = true;

    /// <summary>Order of the "+ Add cooldown" list.</summary>
    public AbilitySort AbilitySort { get; set; } = AbilitySort.Cooldown;

    /// <summary>Master switch for per-entry auto-use. Off by default.</summary>
    public bool AllowAutoUse { get; set; } = false;

    public static string PlanKey(uint territory, uint job) => $"{territory}:{job}";
    public static string TagKey(uint territory, string label) => $"{territory}:{label}";

    public List<PlanEntry> PlanFor(uint territory, uint job)
    {
        var key = PlanKey(territory, job);
        if (!Plans.TryGetValue(key, out var list)) { list = new(); Plans[key] = list; }
        return list;
    }

    public MechanicType TagFor(FightDefinition fight, string label) =>
        TagOverrides.TryGetValue(TagKey(fight.TerritoryId, label), out var t) ? t
        : fight.DefaultTags.TryGetValue(label, out var d) ? d
        : MechanicType.Other;

    public static string SwapKey(uint territory, string eventKey) => $"{territory}:{eventKey}";

    public bool SwapAfter(FightDefinition fight, TimelineEvent e) =>
        SwapOverrides.TryGetValue(SwapKey(fight.TerritoryId, e.Key), out var v) ? v
        : fight.DefaultSwaps?.Contains(e.Key) == true;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
