using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MitPlanner;

/// <summary>A planned use that can't happen because the cooldown won't be back yet.</summary>
public sealed record PlanConflict(
    PlanEntry Entry,
    float PlannedAt,      // estimated fight time the plan wants it pressed
    float ReadyAt,        // when it's actually available again
    TimelineEvent? BlockedBy,   // the earlier planned use that's still on cooldown
    float RecastSeconds,
    int MaxCharges);

/// <summary>
/// QC for a plan: walks each cooldown's planned uses in fight order and simulates its recharge
/// (including charges, which recharge one at a time) to find uses that come too soon.
/// Uses estimated fight time, so checks across a phase change are estimates too.
/// </summary>
public static unsafe class PlanCheck
{
    public static Dictionary<Guid, PlanConflict> Run(Timeline timeline, IEnumerable<PlanEntry> plan, IReadOnlyList<CatalogEntry> catalog)
    {
        var conflicts = new Dictionary<Guid, PlanConflict>();
        var am = ActionManager.Instance();

        foreach (var byAction in plan.GroupBy(p => p.ActionId))
        {
            var ability = catalog.FirstOrDefault(c => c.ActionId == byAction.Key);
            if (ability == null || ability.RecastSeconds <= 0) continue;

            var live = am != null ? am->GetAdjustedActionId(ability.ActionId) : ability.ActionId;
            int maxCharges = Math.Max(1, (int)ActionManager.GetMaxCharges(live, 0));
            var recast = ability.RecastSeconds; // per charge for charge abilities

            // Several entries for the same cooldown on the same mechanic are alternatives
            // (e.g. OT: "Short on me if targeted" / "send Short to the MT if not"): only one
            // can fire, so they count as a single use.
            var groups = byAction.GroupBy(p => p.EventKey).ToDictionary(g => g.First().Id, g => g.ToList());
            var uses = byAction
                .GroupBy(p => p.EventKey)
                .Select(g => g.First())
                .Select(p => (Entry: p, Event: timeline.Events.FirstOrDefault(e => e.Key == p.EventKey)))
                .Where(u => u.Event != null)
                .Select(u => (u.Entry, Event: u.Event!, At: u.Event!.FightTime - Timing.PressBefore(u.Entry)))
                .OrderBy(u => u.At)
                .ToList();

            // Charges start full at the pull; one charge recharges at a time.
            var charges = maxCharges;
            var rechargeStart = 0f;
            TimelineEvent? lastUse = null;

            foreach (var use in uses)
            {
                var t = use.At;
                Advance(ref charges, ref rechargeStart, maxCharges, recast, t);

                if (charges == 0)
                {
                    var readyAt = rechargeStart + recast;
                    foreach (var alt in groups[use.Entry.Id])
                        conflicts[alt.Id] = new PlanConflict(alt, t, readyAt, lastUse, recast, maxCharges);
                    // Assume it gets pressed as soon as it's back, so later checks stay realistic.
                    t = readyAt;
                    Advance(ref charges, ref rechargeStart, maxCharges, recast, t);
                }

                if (charges == maxCharges) rechargeStart = t;
                charges--;
                lastUse = use.Event;
            }
        }

        return conflicts;
    }

    private static void Advance(ref int charges, ref float rechargeStart, int max, float recast, float t)
    {
        while (charges < max && rechargeStart + recast <= t + 0.001f)
        {
            charges++;
            rechargeStart += recast;
        }
    }
}
