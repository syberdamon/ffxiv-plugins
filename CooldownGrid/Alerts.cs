using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Ipc;

namespace CooldownGrid;

/// <summary>
/// Decides which icons pulse. Timing of the next tank buster / raidwide comes from Mit Planner's
/// timeline (IPC "MitPlanner.NextMechanicIn"), or, when Mit Planner has nothing, from BossMod
/// ("BossMod.Timeline.NextTankbusterIn" / "NextRaidwideIn"). Both are optional.
/// </summary>
public static class Alerts
{
    // Upkeep spells -> the status they leave on the tank (applied by you).
    // [Assumption] Status ids from memory: Regen 158, Aspected Benefic 835, Kardion 2605.
    private static readonly Dictionary<uint, uint> Upkeep = new()
    {
        [137] = 158,     // Regen
        [3595] = 835,    // Aspected Benefic
        [24285] = 2605,  // Kardia -> Kardion on the target
    };

    private static ICallGateSubscriber<int, float>? mitPlanner;
    private static ICallGateSubscriber<float>? bmBuster, bmRaidwide;
    private static DateTime cachedAt = DateTime.MinValue;
    private static float buster = float.MaxValue, raidwide = float.MaxValue;

    public static bool ShouldPulse(Configuration config, CooldownGroup group, uint actionId, AbilityStatus status)
    {
        if (!Plugin.Condition[ConditionFlag.InCombat]) return false;

        if (config.PulseMissingUpkeep && Upkeep.TryGetValue(actionId, out var statusId))
            return status.Usable && ActionState.Tank() is { } tank && !HasMyStatus(tank.Id, statusId);

        if (!status.Usable || status.OnCooldown) return false;
        Refresh();
        if (config.PulseBeforeBusters && group.CastOnTank && buster <= config.AlertLeadSeconds) return true;
        if (config.PulseBeforeRaidwides && group.Id == Configuration.DefensiveGroupId && raidwide <= config.AlertLeadSeconds) return true;
        return false;
    }

    private static bool HasMyStatus(ulong objectId, uint statusId)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null || Plugin.ObjectTable.SearchById(objectId) is not IBattleChara who) return true;
        return who.StatusList.Any(s => s.StatusId == statusId && s.SourceId == me.EntityId);
    }

    /// <summary>Ask once per frame-ish (every 0.2 s), not once per icon.</summary>
    private static void Refresh()
    {
        if ((DateTime.UtcNow - cachedAt).TotalSeconds < 0.2) return;
        cachedAt = DateTime.UtcNow;
        mitPlanner ??= Plugin.PluginInterface.GetIpcSubscriber<int, float>("MitPlanner.NextMechanicIn");
        bmBuster ??= Plugin.PluginInterface.GetIpcSubscriber<float>("BossMod.Timeline.NextTankbusterIn");
        bmRaidwide ??= Plugin.PluginInterface.GetIpcSubscriber<float>("BossMod.Timeline.NextRaidwideIn");
        buster = Math.Min(Ask(() => mitPlanner.InvokeFunc(1)), Ask(() => bmBuster.InvokeFunc()));
        raidwide = Math.Min(Ask(() => mitPlanner.InvokeFunc(2)), Ask(() => bmRaidwide.InvokeFunc()));
    }

    private static float Ask(Func<float> f)
    {
        try { var v = f(); return v < 0 ? float.MaxValue : v; }
        catch { return float.MaxValue; }   // that plugin isn't loaded
    }
}
