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

    /// <summary>
    /// OT Sent Mits: cast this on whoever the mechanic targets (normally the MT) instead of
    /// on your current target. Pairs with "If I'm NOT targeted".
    /// </summary>
    public bool SendToTarget { get; set; } = false;
}

/// <summary>Which tank you are this pull; plans are kept separately for each.</summary>
public enum TankRole
{
    MainTank = 0,
    OffTank = 1,
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
    public int Version { get; set; } = 2;

    /// <summary>MT or OT: picks which plan runs. Non-tanks just use the MT slot.</summary>
    public TankRole Role { get; set; } = TankRole.MainTank;

    /// <summary>"territoryId:classJobId:MT|OT" -> plan.</summary>
    public Dictionary<string, List<PlanEntry>> Plans { get; set; } = new();

    /// <summary>"territoryId:label" -> mechanic type, overriding the bundled tags.</summary>
    public Dictionary<string, MechanicType> TagOverrides { get; set; } = new();

    /// <summary>
    /// "territoryId:label" -> type learned in game: a head marker cactbot calls a tank buster/stack
    /// was on a player when this ability hit them. Below your own overrides, above bundled tags.
    /// </summary>
    public Dictionary<string, MechanicType> LearnedTags { get; set; } = new();

    // Display
    public bool ShowTimeline { get; set; } = true;
    public int UpcomingCount { get; set; } = 5;
    public bool ShowOnlyTagged { get; set; } = false;
    public bool LockWindows { get; set; } = false;
    public float CalloutScale { get; set; } = 1.8f;

    /// <summary>Seconds before the press time that the callout starts counting down.</summary>
    public float CalloutHeadsUp { get; set; } = 3f;

    public bool ChatCallouts { get; set; } = false;

    /// <summary>Show the on-screen callout window. Auto-use still runs when this is off.</summary>
    public bool ShowCallouts { get; set; } = true;

    /// <summary>Show column headers on the timeline window (drag them to reorder, edges to resize).</summary>
    public bool ShowTimelineHeaders { get; set; } = true;

    /// <summary>Master switch for the plan: callouts and auto-use. The timeline still shows when off.</summary>
    public bool PlanEnabled { get; set; } = true;

    /// <summary>Order of the "+ Add cooldown" list.</summary>
    public AbilitySort AbilitySort { get; set; } = AbilitySort.Cooldown;

    /// <summary>Master switch for per-entry auto-use. Off by default.</summary>
    public bool AllowAutoUse { get; set; } = false;

    public static string RoleCode(TankRole role) => role == TankRole.OffTank ? "OT" : "MT";
    public static string PlanKey(uint territory, uint job, TankRole role) => $"{territory}:{job}:{RoleCode(role)}";
    public static string TagKey(uint territory, string label) => $"{territory}:{label}";

    public List<PlanEntry> PlanFor(uint territory, uint job) => PlanFor(territory, job, Role);

    public List<PlanEntry> PlanFor(uint territory, uint job, TankRole role)
    {
        var key = PlanKey(territory, job, role);
        if (!Plans.TryGetValue(key, out var list)) { list = new(); Plans[key] = list; }
        return list;
    }

    public MechanicType TagFor(FightDefinition fight, string label) =>
        TagOverrides.TryGetValue(TagKey(fight.TerritoryId, label), out var t) ? t
        : LearnedTags.TryGetValue(TagKey(fight.TerritoryId, label), out var l) ? l
        : fight.DefaultTags.TryGetValue(label, out var d) ? d
        : MechanicType.Other;

    /// <summary>v1 plans had no role ("897:21"); they become MT plans ("897:21:MT").</summary>
    public void Migrate()
    {
        if (Version >= 2) return;
        foreach (var key in new List<string>(Plans.Keys))
        {
            if (key.Split(':').Length != 2) continue;
            var target = key + ":MT";
            if (!Plans.ContainsKey(target)) Plans[target] = Plans[key];
            Plans.Remove(key);
        }
        Version = 2;
        Save();
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
