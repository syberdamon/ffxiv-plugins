using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace MitPlanner;

/// <summary>
/// Named plans (agreed with syberdamon):
///   - Every fight with a timeline gets a "Default" plan for your job (and MT/OT slot), built by the
///     plan generator from the current timeline and tags, and rebuilt whenever that improves
///     (zone entry, job change, each pull start, and while the Plan tab shows it). Never mid-pull.
///   - Editing Default saves your change as "Default - custom" (a copy), which then runs instead.
///     Default itself keeps updating.
///   - Any plan can be picked to run, copied, renamed, edited or deleted. A deleted Default comes
///     back only when the fight has no plans left.
/// </summary>
public sealed class PlanLibrary : IDisposable
{
    public const string DefaultName = "Default";
    public const string CustomName = "Default - custom";

    /// <summary>The Plan tab rebuilds Default at most this often.</summary>
    private const double RebuildEverySeconds = 10;

    private readonly Configuration config;
    private readonly TimelineRunner runner;
    private readonly Dictionary<string, DateTime> lastBuild = new();
    private (uint Territory, uint Job, TankRole Role) watched;

    /// <summary>Generator notes from the last rebuild, by plan key (shown on the Plan tab).</summary>
    public Dictionary<string, List<string>> Notes { get; } = new();

    public PlanLibrary(Configuration config, TimelineRunner runner)
    {
        this.config = config;
        this.runner = runner;
        runner.PullStarted += OnPullStarted;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        runner.PullStarted -= OnPullStarted;
        Plugin.Framework.Update -= OnUpdate;
    }

    /// <summary>Pull start: rebuild with whatever was learned last pull (before anything is pressed).</summary>
    private void OnPullStarted()
    {
        if (runner.Preview || runner.Fight == null || Plugin.ObjectTable.LocalPlayer is not { } me) return;
        EnsureDefault(runner.Fight, me.ClassJob.RowId, config.Role, force: true);
    }

    /// <summary>Entered a zone with a timeline, or changed job/slot: make sure Default exists and is current.</summary>
    private void OnUpdate(IFramework framework)
    {
        if (runner.Preview || runner.Fight == null || Plugin.ObjectTable.LocalPlayer is not { } me) return;
        var now = (runner.Fight.TerritoryId, me.ClassJob.RowId, config.Role);
        if (now == watched) return;
        watched = now;
        if (!runner.Running) EnsureDefault(runner.Fight, me.ClassJob.RowId, config.Role, force: true);
    }

    /// <summary>Jobs the generator knows (tanks and healers). Others get no Default.</summary>
    public static bool CanGenerate(uint jobId) => TankKits.For(jobId) != null || HealerKits.IsHealer(jobId);

    /// <summary>
    /// Create Default if it's missing (unless you deleted it and other plans remain), or rebuild it
    /// if the generator now gives something different. Only for your current job: the generator
    /// plans with your job's abilities at the fight's level. Skipped while this fight is running.
    /// </summary>
    public void EnsureDefault(FightDefinition fight, uint jobId, TankRole role, bool force = false)
    {
        try
        {
            if (!CanGenerate(jobId) || Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId != jobId) return;
            if (runner.Running && !runner.Preview && runner.Fight == fight) return; // never swap entries mid-pull

            var key = Configuration.PlanKey(fight.TerritoryId, jobId, role);
            if (!force && lastBuild.TryGetValue(key, out var at) && (DateTime.UtcNow - at).TotalSeconds < RebuildEverySeconds) return;
            lastBuild[key] = DateTime.UtcNow;

            var set = config.SetFor(fight.TerritoryId, jobId, role);
            var def = set.Plans.FirstOrDefault(p => p.IsDefault);
            if (def == null && set.DefaultDeleted && set.Plans.Count > 0) return;

            var result = PlanGenerator.Generate(config, fight, Fights.Load(fight), jobId, role, ActionCatalog.ForFight(fight));
            Notes[key] = result.Notes;
            if (def == null)
            {
                def = new NamedPlan { Name = DefaultName, IsDefault = true, Entries = result.Plan };
                set.Plans.Insert(0, def);
                set.DefaultDeleted = false;
                Configuration.ActiveOf(set); // picks Default only if nothing else was running
                config.Save();
                return;
            }
            if (Same(def.Entries, result.Plan)) return;
            def.Entries = result.Plan;
            config.Save();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Couldn't build the Default plan for {fight.Name}");
        }
    }

    /// <summary>You edited Default: save the edited copy as "Default - custom" (2, 3, ... if taken) and run it.</summary>
    public NamedPlan ForkDefault(PlanSet set, NamedPlan def, List<PlanEntry> edited)
    {
        var custom = new NamedPlan { Name = UniqueName(set, CustomName), FromDefault = true, Entries = edited };
        set.Plans.Insert(set.Plans.IndexOf(def) + 1, custom);
        set.ActiveId = custom.Id;
        config.Save();
        return custom;
    }

    public NamedPlan Add(PlanSet set, string name, List<PlanEntry> entries)
    {
        var plan = new NamedPlan { Name = UniqueName(set, name), Entries = entries };
        set.Plans.Add(plan);
        set.ActiveId = plan.Id;
        config.Save();
        return plan;
    }

    public void Delete(PlanSet set, NamedPlan plan)
    {
        set.Plans.Remove(plan);
        if (plan.IsDefault) set.DefaultDeleted = set.Plans.Count > 0;
        if (set.Plans.Count == 0) set.DefaultDeleted = false;
        if (set.ActiveId == plan.Id) { set.ActiveId = Guid.Empty; Configuration.ActiveOf(set); }
        config.Save();
    }

    /// <summary>"Name", or "Name 2", "Name 3", ... if taken.</summary>
    public static string UniqueName(PlanSet set, string name)
    {
        name = name.Trim();
        if (name.Length == 0) name = "Plan";
        if (!set.Plans.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return name;
        for (var i = 2; ; i++)
            if (!set.Plans.Any(p => p.Name.Equals($"{name} {i}", StringComparison.OrdinalIgnoreCase))) return $"{name} {i}";
    }

    /// <summary>Copy entries. New IDs for a real copy; same IDs for a scratch copy that's being edited.</summary>
    public static List<PlanEntry> Clone(IEnumerable<PlanEntry> entries, bool newIds) => entries.Select(p => new PlanEntry
    {
        Id = newIds ? Guid.NewGuid() : p.Id,
        EventKey = p.EventKey,
        ActionId = p.ActionId,
        LeadSeconds = p.LeadSeconds,
        AutoUse = p.AutoUse,
        Condition = p.Condition,
        SendToTarget = p.SendToTarget,
        Placement = p.Placement,
        AllowGcdAutoUse = p.AllowGcdAutoUse,
    }).ToList();

    private static bool Same(List<PlanEntry> a, List<PlanEntry> b)
    {
        static string Sig(PlanEntry p) =>
            $"{p.EventKey}|{p.ActionId}|{p.LeadSeconds:0.00}|{p.AutoUse}|{p.Condition}|{p.SendToTarget}|{p.Placement}|{p.AllowGcdAutoUse}";
        return a.Count == b.Count && a.Select(Sig).OrderBy(x => x).SequenceEqual(b.Select(Sig).OrderBy(x => x));
    }
}
