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

    /// <summary>When a tank buster is learned in game, add mits for it to your plan.</summary>
    public bool AutoPlanLearned { get; set; } = true;

    /// <summary>
    /// Tank-buster marker on you (or a shared-buster marker on a tank) and nothing planned covers
    /// it: call out (and, with auto-use allowed, press) the best mits that are off cooldown.
    /// </summary>
    public bool ReactToBusterMarkers { get; set; } = true;

    /// <summary>Trash mode: automatic tank mitigation for dungeon pulls (see TrashMode.cs).</summary>
    public TrashSettings Trash { get; set; } = new();

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


/// <summary>Trash mode settings. Defaults from docs/DUNGEON_MIT_RULES.md, agreed with syberdamon.</summary>
[Serializable]
public class TrashSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Write a run log (pluginConfigs/MitPlanner/trashlog) for tuning.</summary>
    public bool Log { get; set; } = true;

    // ---- when a pull counts as "set"
    public int MinMobs { get; set; } = 3;
    public float StationarySeconds { get; set; } = 1.5f;
    public float GatherRadius { get; set; } = 5f;
    public float GatherShare { get; set; } = 0.7f;      // share of mobs on you within GatherRadius
    /// <summary>Below this HP (with 2+ mobs on you) it acts even while you're moving/gathering.</summary>
    public float MovingHpPercent { get; set; } = 60f;

    // ---- rotation
    public float HeavyMinPullSeconds { get; set; } = 12f;   // 40% only if the pull lasts this much longer
    public float GapMinPullSeconds { get; set; } = 8f;      // refill an expired big mit only if this much is left
    public float WindDownSeconds { get; set; } = 6f;        // no new big mits under this
    public int BigPullMobs { get; set; } = 5;               // 40% goes first on pulls this big
    public float EscalateHpPercent { get; set; } = 80f;     // below this, add the next layer sooner
    public float HealHpPercent { get; set; } = 70f;
    public bool PartyMitsAsHeals { get; set; } = true;
    public float WarRampartAfterBloodwhettingHp { get; set; } = 70f; // WAR: Rampart waits for Bloodwhetting unless HP below this

    // ---- emergency / invuln
    public float EmergencyHpPercent { get; set; } = 35f;
    /// <summary>Invuln when this many seconds of the current incoming damage (no heals) would kill you and no major mit is available.</summary>
    public float InvulnLethalSeconds { get; set; } = 2f;
    public bool InvulnPaladin { get; set; } = true;     // Hallowed Ground
    public bool InvulnWarrior { get; set; } = true;     // Holmgang
    public bool InvulnDarkKnight { get; set; } = false; // Living Dead (needs heals after)
    public bool InvulnGunbreaker { get; set; } = false; // Superbolide (drops you to 1 HP)
}