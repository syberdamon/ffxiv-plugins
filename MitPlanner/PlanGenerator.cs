using System.Collections.Generic;
using System.Linq;

namespace MitPlanner;

/// <summary>
/// Builds a starter tank plan from rules (agreed with syberdamon):
///   MT, every tank buster: Short, plus Big if it'll be back in time, otherwise Rampart + Secondary.
///   OT, every tank buster: send the Short-style mit to whoever it targets; also Short on yourself
///       if it targets you.
///   Raid-wides: each gets Reprisal from one tank and party mit from the other.
///       #1 MT Reprisal + OT party mit, #2 MT party mit + OT Reprisal, and so on.
///   Invulns are never planned.
/// Availability is decided by the same cooldown QC the Plan tab uses.
/// </summary>
public static class PlanGenerator
{
    // Lead times (syberdamon): Short lands 2.0s before the buster; each heavier mit goes
    // 1.25s before the one after it, so presses don't collide on animation lock.
    //   Big available:  Big 3.25s -> Short 2.0s
    //   Big not back:   Secondary 4.0s -> Rampart 3.25s -> Short 2.0s
    //                   (Rampart takes Big's slot; Secondary goes 0.75s ahead of Rampart)
    //   Raid-wides:     Reprisal 5.0s -> party mit 3.75s
    private const float Spacing = 1.25f;
    private const float SecondaryAhead = 0.75f;
    private const float ShortLead = 2f;
    private const float BigLead = ShortLead + Spacing;            // 3.25
    private const float RampartLead = BigLead;                    // 3.25
    private const float SecondaryLead = RampartLead + SecondaryAhead; // 4.0
    private const float ReprisalLead = 5f;
    private const float PartyMitLead = ReprisalLead - Spacing;    // 3.75

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

        var busters = FirstPass(config, fight, timeline, MechanicType.TankBuster);
        if (busters.Count == 0) notes.Add("This fight has no mechanics tagged as tank busters.");

        foreach (var ev in busters)
        {
            if (role == TankRole.MainTank)
            {
                if (hasShort) TryAdd(plan, timeline, catalog, notes, Entry(ev, kit.Short, ShortLead, TargetCondition.IfTargeted), ev, "Short");

                // Big if it'll be back in time; otherwise Rampart + Secondary.
                var bigOk = hasBig && TryAdd(plan, timeline, catalog, null, Entry(ev, kit.Big, BigLead, TargetCondition.IfTargeted), ev, "Big");
                if (!bigOk)
                {
                    var rampartOk = hasRampart && TryAdd(plan, timeline, catalog, null, Entry(ev, kit.Rampart, RampartLead, TargetCondition.IfTargeted), ev, "Rampart");
                    if (hasSecondary) TryAdd(plan, timeline, catalog, notes, Entry(ev, kit.Secondary, SecondaryLead, TargetCondition.IfTargeted), ev, "Secondary");
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

        // Raid-wides: every one gets one Reprisal and one party mit, from different tanks.
        //   #1: MT Reprisal + OT party mit;  #2: MT party mit + OT Reprisal;  then repeat.
        // Both tanks' cooldowns are simulated together (Reprisal and every tank's party mit
        // share the same recasts), so the MT's and the OT's generated plans dovetail.
        var hasReprisal = Has(TankKits.ReprisalId, "Reprisal");
        var hasParty = Has(kit.PartyMit, "Party mit");
        var raidwides = FirstPass(config, fight, timeline, MechanicType.Raidwide);
        var reprisalRecast = catalog.FirstOrDefault(c => c.ActionId == TankKits.ReprisalId)?.RecastSeconds ?? 60f;
        var partyRecast = catalog.FirstOrDefault(c => c.ActionId == kit.PartyMit)?.RecastSeconds ?? 90f;
        var me = role == TankRole.MainTank ? 0 : 1;
        var repReady = new[] { float.MinValue, float.MinValue };   // [MT, OT]
        var partyReady = new[] { float.MinValue, float.MinValue };
        var reprisalTank = 0;                                       // who has Reprisal on the next one
        var gaps = new List<string>();

        foreach (var ev in raidwides)
        {
            var repAt = ev.FightTime - ReprisalLead;
            var partyAt = ev.FightTime - PartyMitLead;
            bool RepOk(int tank) => repReady[tank] <= repAt;
            bool PartyOk(int tank) => partyReady[tank] <= partyAt;

            // Planned split, else the swapped split, else whatever is back.
            int? rep = null, party = null;
            var other = 1 - reprisalTank;
            if (RepOk(reprisalTank) && PartyOk(other)) { rep = reprisalTank; party = other; }
            else if (RepOk(other) && PartyOk(reprisalTank)) { rep = other; party = reprisalTank; }
            else
            {
                rep = RepOk(reprisalTank) ? reprisalTank : RepOk(other) ? other : (int?)null;
                party = rep is { } r && PartyOk(1 - r) ? 1 - r : PartyOk(reprisalTank) ? reprisalTank : PartyOk(other) ? other : (int?)null;
                var missing = new List<string>();
                if (rep == null) missing.Add("no Reprisal");
                if (party == null) missing.Add("no party mit");
                if (rep != null && rep == party) missing.Add($"{Configuration.RoleCode(rep == 0 ? TankRole.MainTank : TankRole.OffTank)} covers both");
                gaps.Add($"{Label(ev)}: {string.Join(", ", missing)}");
            }

            if (rep is { } rt) { repReady[rt] = repAt + reprisalRecast; reprisalTank = 1 - rt; }
            if (party is { } pt) partyReady[pt] = partyAt + partyRecast;

            if (rep == me && hasReprisal)
                TryAdd(plan, timeline, catalog, notes, Entry(ev, TankKits.ReprisalId, ReprisalLead, TargetCondition.Always), ev, "Reprisal");
            if (party == me && hasParty)
                TryAdd(plan, timeline, catalog, notes, Entry(ev, kit.PartyMit, PartyMitLead, TargetCondition.Always), ev, "Party mit");
        }
        if (raidwides.Count > 0)
            notes.Add($"Raid-wides: {raidwides.Count}, each with one tank's Reprisal and the other's party mit (MT Reprisal first, then they swap).");
        if (gaps.Count > 0)
            notes.Add("Raid-wides too close together for full cover: " + string.Join("; ", gaps) + ".");

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
    /// Mechanics of one type in fight order, first pass only: lines after a loop's jump-back
    /// are display copies of earlier mechanics, so they're skipped until the next phase.
    /// </summary>
    private static List<TimelineEvent> FirstPass(Configuration config, FightDefinition fight, Timeline timeline, MechanicType type)
    {
        var result = new List<TimelineEvent>();
        var skipping = false;
        TimelineEvent? prev = null;
        foreach (var e in timeline.Events)
        {
            if (prev != null && e.Time - prev.Time > 30) skipping = false; // new phase
            if (!skipping && !e.Hidden && config.TagFor(fight, e.Label) == type) result.Add(e);
            if (e.Jump is { } j && j < e.Time) skipping = true;
            prev = e;
        }
        return result;
    }

    private static string Label(TimelineEvent e) => $"{e.Label} #{e.Occurrence}";
}
