using System.Collections.Generic;
using System.Linq;

namespace MitPlanner;

/// <summary>
/// Builds a starter tank plan from rules (agreed with syberdamon):
///   MT, every tank buster: Short, plus Big if it'll be back in time, otherwise Rampart + Secondary.
///   OT, every tank buster: send the Short-style mit to whoever it targets; also Short on yourself
///       if it targets you.
///   Invulns are never planned. Raid-wides aren't planned yet.
/// Availability is decided by the same cooldown QC the Plan tab uses.
/// </summary>
public static class PlanGenerator
{
    // [Assumption] Lead times: short mits last ~4-8s, so press 2s before; big ones 3s.
    private const float ShortLead = 2f;
    private const float BigLead = 3f;

    public sealed record Result(List<PlanEntry> Plan, List<string> Notes);

    public static Result Generate(Configuration config, FightDefinition fight, Timeline timeline,
        uint jobId, TankRole role, IReadOnlyList<CatalogEntry> catalog)
    {
        var plan = new List<PlanEntry>();
        var notes = new List<string>();

        var kit = TankKits.For(jobId);
        if (kit == null)
        {
            notes.Add("Starter plans are tank-only for now.");
            return new(plan, notes);
        }

        bool Has(uint id, string what)
        {
            if (catalog.Any(c => c.ActionId == id)) return true;
            notes.Add($"{what} (action {id}) isn't available on this job at your level, or the ID is wrong; skipped.");
            return false;
        }

        var hasShort = Has(kit.Short, "Short mit");
        var hasBig = Has(kit.Big, "Big mit");
        var hasRampart = Has(kit.Rampart, "Rampart");
        var hasSecondary = Has(kit.Secondary, "Secondary mit");
        var hasSent = role == TankRole.OffTank && Has(kit.Sent, "OT Sent Mit");

        var busters = FirstPassBusters(config, fight, timeline);
        if (busters.Count == 0)
        {
            notes.Add("This fight has no mechanics tagged as tank busters.");
            return new(plan, notes);
        }

        foreach (var ev in busters)
        {
            if (role == TankRole.MainTank)
            {
                if (hasShort) TryAdd(plan, timeline, catalog, notes, Entry(ev, kit.Short, ShortLead, TargetCondition.IfTargeted), ev, "Short");

                // Big if it'll be back in time; otherwise Rampart + Secondary.
                var bigOk = hasBig && TryAdd(plan, timeline, catalog, null, Entry(ev, kit.Big, BigLead, TargetCondition.IfTargeted), ev, "Big");
                if (!bigOk)
                {
                    var rampartOk = hasRampart && TryAdd(plan, timeline, catalog, null, Entry(ev, kit.Rampart, BigLead, TargetCondition.IfTargeted), ev, "Rampart");
                    if (hasSecondary) TryAdd(plan, timeline, catalog, notes, Entry(ev, kit.Secondary, BigLead, TargetCondition.IfTargeted), ev, "Secondary");
                    if (!rampartOk) notes.Add($"{Label(ev)}: neither Big nor Rampart will be back in time.");
                }
            }
            else
            {
                // OT: help the MT; cover yourself only if the buster turns out to be on you.
                if (hasSent)
                {
                    var sent = Entry(ev, kit.Sent, ShortLead, TargetCondition.IfNotTargeted);
                    sent.SendToTarget = true;
                    TryAdd(plan, timeline, catalog, notes, sent, ev, "OT Sent Mit");
                }
                if (hasShort) TryAdd(plan, timeline, catalog, notes, Entry(ev, kit.Short, ShortLead, TargetCondition.IfTargeted), ev, "Short");
            }
        }

        if (kit.ShortUsesResource)
            notes.Add($"{kit.Job}'s Short mit costs a resource (gauge/MP) that the cooldown check can't see; make sure you'll have it.");

        return new(plan, notes);
    }

    private static PlanEntry Entry(TimelineEvent ev, uint actionId, float lead, TargetCondition condition) => new()
    {
        EventKey = ev.Key,
        ActionId = actionId,
        LeadSeconds = lead,
        Condition = condition,
        AutoUse = true, // still needs the Auto: ON switch
    };

    /// <summary>Add the entry only if the cooldown QC says it'll be ready.</summary>
    private static bool TryAdd(List<PlanEntry> plan, Timeline timeline, IReadOnlyList<CatalogEntry> catalog,
        List<string>? notes, PlanEntry entry, TimelineEvent ev, string what)
    {
        plan.Add(entry);
        if (!PlanCheck.Run(timeline, plan, catalog).ContainsKey(entry.Id)) return true;
        plan.Remove(entry);
        notes?.Add($"{Label(ev)}: {what} won't be back in time; left out.");
        return false;
    }

    /// <summary>
    /// Tank busters in fight order, first pass only: lines after a loop's jump-back are
    /// display copies of earlier mechanics, so they're skipped until the next phase.
    /// </summary>
    private static List<TimelineEvent> FirstPassBusters(Configuration config, FightDefinition fight, Timeline timeline)
    {
        var result = new List<TimelineEvent>();
        var skipping = false;
        TimelineEvent? prev = null;
        foreach (var e in timeline.Events)
        {
            if (prev != null && e.Time - prev.Time > 30) skipping = false; // new phase
            if (!skipping && !e.Hidden && config.TagFor(fight, e.Label) == MechanicType.TankBuster) result.Add(e);
            if (e.Jump is { } j && j < e.Time) skipping = true;
            prev = e;
        }
        return result;
    }

    private static string Label(TimelineEvent e) => $"{e.Label} #{e.Occurrence}";
}
