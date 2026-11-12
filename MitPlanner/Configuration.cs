using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// Where it goes: on the tank, on you, on the boss, or (ground-targeted heals: Asylum, Sacred
    /// Soil, Earthly Star, Liturgy of the Bell) at the arena center. Default = the game's choice.
    /// </summary>
    public EntryPlacement Placement { get; set; } = EntryPlacement.Default;

    /// <summary>
    /// GCD spells (Adloquium, Eukrasian Diagnosis, ...) are callouts only unless this is on:
    /// auto-pressing a GCD can cut off a cast or delay your rotation.
    /// </summary>
    public bool AllowGcdAutoUse { get; set; } = false;
}

public enum EntryPlacement
{
    Default = 0,
    OnTank = 1,
    OnSelf = 2,
    OnBoss = 3,
    ArenaCenter = 4,
}

/// <summary>A saved world position (arena centers).</summary>
[Serializable]
public class SavedPoint
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public System.Numerics.Vector3 ToVector() => new(X, Y, Z);
    public static SavedPoint From(System.Numerics.Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };
}

/// <summary>
/// One named plan for a fight + job (+ MT/OT for tanks). "Default" is generated from the current
/// timeline and tags and rebuilt as they improve; editing it saves a "Default - custom" copy.
/// </summary>
[Serializable]
public class NamedPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>The auto-generated plan: rebuilt from the generator, never edited in place by you.</summary>
    public bool IsDefault { get; set; }
    /// <summary>Made by editing Default ("Default - custom"): preferred over Default.</summary>
    public bool FromDefault { get; set; }
    public List<PlanEntry> Entries { get; set; } = new();
}

/// <summary>Every plan for one fight + job (+ MT/OT), and which one runs.</summary>
[Serializable]
public class PlanSet
{
    public List<NamedPlan> Plans { get; set; } = new();
    public Guid ActiveId { get; set; }
    /// <summary>You deleted Default: it's only rebuilt once this fight has no plans left.</summary>
    public bool DefaultDeleted { get; set; }
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
    /// <summary>Healers: when the mechanic targets a tank (not a buster on someone else).</summary>
    IfTankTargeted = 3,
}

public enum AbilitySort
{
    Cooldown = 0,
    Name = 1,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;

    /// <summary>MT or OT: picks which plan runs. Non-tanks just use the MT slot.</summary>
    public TankRole Role { get; set; } = TankRole.MainTank;

    /// <summary>"territoryId:classJobId:MT|OT|H" -> that fight's plans for that job (and tank slot).</summary>
    public Dictionary<string, PlanSet> PlanSets { get; set; } = new();

    /// <summary>Before v4: one unnamed plan per key. Moved into <see cref="PlanSets"/> as "Custom"; kept only to read old settings.</summary>
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

    /// <summary>
    /// Healers: new plan entries (and generated ones) finish this many seconds before the hit.
    /// Cast time, a Eukrasia first, and Earthly Star's maturing are added on top automatically.
    /// </summary>
    public float HealerLeadSeconds { get; set; } = 3f;

    /// <summary>Arena centers you saved, by zone ("Set arena center here").</summary>
    public Dictionary<uint, SavedPoint> ArenaCenters { get; set; } = new();

    /// <summary>Trash mode: automatic tank mitigation for dungeon pulls (see TrashMode.cs).</summary>
    public TrashSettings Trash { get; set; } = new();

    public static string RoleCode(TankRole role) => role == TankRole.OffTank ? "OT" : "MT";
    /// <summary>"MT"/"OT" for tanks (and everyone else's old plans), "H" for healers (one plan per job).</summary>
    public static string PlanRoleCode(uint job, TankRole role) => HealerKits.IsHealer(job) ? "H" : RoleCode(role);
    public static string PlanKey(uint territory, uint job, TankRole role) => $"{territory}:{job}:{PlanRoleCode(job, role)}";
    public static string TagKey(uint territory, string label) => $"{territory}:{label}";

    public PlanSet SetFor(uint territory, uint job) => SetFor(territory, job, Role);

    public PlanSet SetFor(uint territory, uint job, TankRole role)
    {
        var key = PlanKey(territory, job, role);
        if (!PlanSets.TryGetValue(key, out var set)) { set = new(); PlanSets[key] = set; }
        return set;
    }

    /// <summary>
    /// The plan that runs: the one you picked, else a "Default - custom", else Default, else the first.
    /// </summary>
    public static NamedPlan? ActiveOf(PlanSet set)
    {
        var picked = set.Plans.FirstOrDefault(p => p.Id == set.ActiveId);
        if (picked != null) return picked;
        picked = set.Plans.LastOrDefault(p => p.FromDefault) ?? set.Plans.FirstOrDefault(p => p.IsDefault) ?? set.Plans.FirstOrDefault();
        if (picked != null) set.ActiveId = picked.Id;
        return picked;
    }

    /// <summary>The running plan's entries (an empty, unsaved list when there's no plan).</summary>
    public List<PlanEntry> PlanFor(uint territory, uint job) => PlanFor(territory, job, Role);

    public List<PlanEntry> PlanFor(uint territory, uint job, TankRole role) =>
        ActiveOf(SetFor(territory, job, role))?.Entries ?? new List<PlanEntry>();

    public MechanicType TagFor(FightDefinition fight, string label) =>
        TagOverrides.TryGetValue(TagKey(fight.TerritoryId, label), out var t) ? t
        : LearnedTags.TryGetValue(TagKey(fight.TerritoryId, label), out var l) ? l
        : fight.DefaultTags.TryGetValue(label, out var d) ? d
        : MechanicType.Other;

    /// <summary>v1 plans had no role ("897:21"); they become MT plans ("897:21:MT").</summary>
    /// <summary>v3: healers' plans lived in the MT slot ("897:24:MT"); they move to "897:24:H".</summary>
    public void Migrate()
    {
        if (Version >= 4) return;
        if (Version < 2)
        {
            foreach (var key in new List<string>(Plans.Keys))
            {
                if (key.Split(':').Length != 2) continue;
                var target = key + ":MT";
                if (!Plans.ContainsKey(target)) Plans[target] = Plans[key];
                Plans.Remove(key);
            }
        }
        foreach (var key in new List<string>(Plans.Keys))
        {
            var parts = key.Split(':');
            if (parts.Length != 3 || parts[2] != "MT" || !uint.TryParse(parts[1], out var job) || !HealerKits.IsHealer(job)) continue;
            var target = $"{parts[0]}:{parts[1]}:H";
            if (!Plans.ContainsKey(target)) Plans[target] = Plans[key];
            Plans.Remove(key);
        }
        // v4: named plans. Each old plan becomes "Custom" and stays the one that runs.
        foreach (var (key, list) in Plans)
        {
            if (list.Count == 0 || PlanSets.ContainsKey(key)) continue;
            var custom = new NamedPlan { Name = "Custom", Entries = list };
            PlanSets[key] = new PlanSet { Plans = { custom }, ActiveId = custom.Id };
        }
        Plans.Clear();
        Version = 4;
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
    /// <summary>While gathering, press the short mit (Bloodwhetting etc.) below this HP with enough mobs on
    /// you. From the Lunar Subterrane log (2026-11-04): HP fell from ~90% to 57-65% during big gathers.</summary>
    public float PullShortHpPercent { get; set; } = 80f;
    public int PullShortMinMobs { get; set; } = 3;
    /// <summary>While gathering with no short mit available, Rampart goes out below this HP (Reprisal goes first,
    /// below PullShortHpPercent).</summary>
    public float PullRampartHpPercent { get; set; } = 70f;
    /// <summary>
    /// Last resort while gathering a big pack: the short mit, Reprisal and Rampart are all down, so press
    /// the big mit before the pull is set. Narrow on purpose (Hullbreaker Isle WAR50, 2026-11-08: 13 mobs
    /// gathered 25 s after the previous pack, HP 100% -> 56% with nothing to press, then a death). In the
    /// logs from 2026-11-03..10 these conditions held for at most a few seconds per run.
    /// </summary>
    public bool PullBigWhenOut { get; set; } = true;

    /// <summary>
    /// Look ahead before using the big mit: keep it for the next pack or the boss when that's worth
    /// more than what it prevents now (dungeon route maps + MitScorer). Survival always wins.
    /// </summary>
    public bool LookaheadHold { get; set; } = true;
    public int PullBigMinMobs { get; set; } = 6;
    public float PullBigHpPercent { get; set; } = 70f;
    /// <summary>...and still losing at least this much HP per second (net of heals).</summary>
    public float PullBigDrainPctPerSec { get; set; } = 2f;
    /// <summary>No new big mit while HP is at least this and incoming damage is below IdleDamagePctPerSec
    /// (Rampart was going out at 100% HP, ~0%/s, with the pull nearly dead).</summary>
    public float IdleHpPercent { get; set; } = 95f;
    public float IdleDamagePctPerSec { get; set; } = 3f;

    // ---- rotation
    public float HeavyMinPullSeconds { get; set; } = 12f;   // 40% only if the pull lasts this much longer
    public float GapMinPullSeconds { get; set; } = 8f;      // refill an expired big mit only if this much is left
    public float WindDownSeconds { get; set; } = 6f;        // no new big mits under this
    public int BigPullMobs { get; set; } = 5;               // 40% goes first on pulls this big
    public float EscalateHpPercent { get; set; } = 80f;     // below this, add the next layer sooner
    public float HealHpPercent { get; set; } = 70f;
    public bool PartyMitsAsHeals { get; set; } = true;
    public float WarRampartAfterBloodwhettingHp { get; set; } = 70f; // WAR: Rampart waits for Bloodwhetting unless HP below this

    /// <summary>
    /// WAR: once its short cooldown heals on hit (Raw Intuition, later Bloodwhetting), that heal
    /// caps HP on multi-mob AoE pulls, so it alone carries the pull. Other mits/heals only once it
    /// has worn off AND HP is below <see cref="WarOtherMitsBelowHpPct"/>. (Renamed from the
    /// 0.9.1 settings so the new defaults apply.)
    /// </summary>
    public bool WarBloodwhettingOnly { get; set; } = true;
    public int WarShortHealLevel { get; set; } = 56;       // [Verify] Raw Intuition (56) heals on hit
    public float WarOtherMitsBelowHpPct { get; set; } = 70f;
    /// <summary>WAR: when Bloodwhetting falls off and is still recasting, chain the next mit at once on pulls this big...</summary>
    public int WarChainMinMobs { get; set; } = 4;
    /// <summary>...that last at least this much longer (s).</summary>
    public float WarChainMinPullSeconds { get; set; } = 10f;

    // ---- emergency / invuln
    public float EmergencyHpPercent { get; set; } = 35f;
    /// <summary>Invuln when this many seconds of the current incoming damage (no heals) would kill you and no major mit is available.</summary>
    public float InvulnLethalSeconds { get; set; } = 2f;
    public bool InvulnPaladin { get; set; } = true;     // Hallowed Ground
    public bool InvulnWarrior { get; set; } = true;     // Holmgang
    public bool InvulnDarkKnight { get; set; } = false; // Living Dead (needs heals after)
    public bool InvulnGunbreaker { get; set; } = false; // Superbolide (drops you to 1 HP)
}