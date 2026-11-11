using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Action = Lumina.Excel.Sheets.Action;

namespace MitPlanner;

/// <summary>
/// When to start pressing a plan entry. An entry's LeadSeconds is when it should be DONE, before
/// the hit. On top of that:
///   + its cast time (live, with your spell speed),
///   + one GCD for each button that has to go first (Sage: Eukrasia before Eukrasian Diagnosis),
///   + for a GCD spell, one more GCD of room, since the GCD may still be rolling when its time
///     comes (the press is queued; it then lands somewhere in that window, so "at least N s
///     before" rather than exactly),
///   + Earthly Star: 10 s to mature before it's detonated at the lead time.
/// Tank mits are instant off-GCDs, so for them this is just LeadSeconds, as before.
/// </summary>
public static unsafe class Timing
{
    /// <summary>Seconds before the hit to start pressing.</summary>
    public static float PressBefore(PlanEntry entry) => entry.LeadSeconds + Extra(entry.ActionId);

    /// <summary>Cast time + GCDs that go first + GCD room + maturing time.</summary>
    public static float Extra(uint actionId)
    {
        var extra = CastSeconds(actionId) + HealerKits.MatureSeconds(actionId);
        var prereq = HealerKits.Prereq(actionId);
        if (prereq != 0) extra += RecastSeconds(prereq);
        if (IsGcd(actionId)) extra += RecastSeconds(actionId);
        return extra;
    }

    public static bool IsGcd(uint actionId) =>
        Plugin.DataManager.GetExcelSheet<Action>().GetRowOrDefault(actionId) is { } r && r.CooldownGroup == 58;

    /// <summary>Live cast time (spell speed, swiftcast-style procs ignored), else the sheet's.</summary>
    public static float CastSeconds(uint actionId)
    {
        try
        {
            var ms = ActionManager.GetAdjustedCastTime(ActionType.Action, actionId, false);
            if (ms > 0) return ms / 1000f;
            if (ms == 0) return 0f;
        }
        catch { /* not in game yet */ }
        return Plugin.DataManager.GetExcelSheet<Action>().GetRowOrDefault(actionId) is { } r ? r.Cast100ms / 10f : 0f;
    }

    /// <summary>Live recast (GCD length with your spell speed), else the sheet's.</summary>
    public static float RecastSeconds(uint actionId)
    {
        try
        {
            var ms = ActionManager.GetAdjustedRecastTime(ActionType.Action, actionId);
            if (ms > 0) return ms / 1000f;
        }
        catch { /* not in game yet */ }
        return Plugin.DataManager.GetExcelSheet<Action>().GetRowOrDefault(actionId) is { } r ? r.Recast100ms / 10f : 2.5f;
    }
}
