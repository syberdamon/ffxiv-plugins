using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace CooldownGrid;

/// <summary>Live cooldown/charge state for one ability.</summary>
public readonly record struct AbilityStatus(
    float Remaining,      // seconds until usable (next charge, for charge abilities)
    int Charges,          // current charges (0 if not a charge ability)
    int MaxCharges,       // 1 for normal abilities
    bool OnCooldown,      // no use available right now because of cooldown
    bool Usable);         // the game would accept a press right now

public static unsafe class ActionState
{
    public static AbilityStatus Get(uint baseActionId, ulong targetObjectId = 0xE000_0000)
    {
        var am = ActionManager.Instance();
        if (am == null) return default;

        if (ActionCatalog.IsGeneralAction(baseActionId)) return GetLimitBreak(am);

        var id = am->GetAdjustedActionId(baseActionId);
        var total = am->GetRecastTime(ActionType.Action, id);
        var elapsed = am->GetRecastTimeElapsed(ActionType.Action, id);
        var remainingAll = total > 0 ? total - elapsed : 0;

        int max = ActionManager.GetMaxCharges(id, 0);
        if (max < 1) max = 1;

        float remaining;
        int charges;
        bool onCooldown;
        if (max > 1)
        {
            charges = (int)am->GetCurrentCharges(id);
            // The recast timer covers all charges; the next charge arrives after the last slice.
            var perCharge = total / max;
            remaining = charges < max && remainingAll > 0 ? remainingAll - perCharge * (max - charges - 1) : 0;
            onCooldown = charges == 0;
        }
        else
        {
            charges = 0;
            remaining = remainingAll;
            onCooldown = am->IsRecastTimerActive(ActionType.Action, id) && remaining > 0;
        }

        var usable = am->GetActionStatus(ActionType.Action, id, targetObjectId) == 0;
        return new AbilityStatus(remaining, charges, max, onCooldown, usable);
    }

    /// <summary>
    /// Limit Break: filled bars show as "charges" (bottom-right number), max = bars available
    /// in this content; dimmed while the gauge has no full bar.
    /// </summary>
    private static AbilityStatus GetLimitBreak(ActionManager* am)
    {
        var lb = &UIState.Instance()->LimitBreakController;
        int bars = lb->BarUnits > 0 ? lb->CurrentUnits / lb->BarUnits : 0;
        int max = lb->BarCount;
        var usable = am->GetActionStatus(ActionType.GeneralAction, ActionCatalog.LimitBreakGeneralAction) == 0;
        return new AbilityStatus(0, bars, max > 1 ? max : 1, bars == 0, usable);
    }

    /// <summary>Same request a hotbar button sends; targeting is resolved by the game.</summary>
    public static bool Use(uint baseActionId) => Use(baseActionId, 0xE000_0000);

    /// <summary>Use on a specific target (OT Sent Mits: cast on the co-tank without targeting them).</summary>
    public static bool Use(uint baseActionId, ulong targetObjectId)
    {
        var am = ActionManager.Instance();
        if (am == null) return false;
        if (ActionCatalog.IsGeneralAction(baseActionId))
            return am->UseAction(ActionType.GeneralAction, baseActionId & ~ActionCatalog.GeneralActionFlag);
        return am->UseAction(ActionType.Action, am->GetAdjustedActionId(baseActionId), targetObjectId);
    }

    // ClassJob row ids for tanks: GLA/PLD, MRD/WAR, DRK, GNB.
    private static readonly System.Collections.Generic.HashSet<uint> TankJobs = new() { 1, 19, 3, 21, 32, 37 };

    /// <summary>
    /// The tank who's tanking: the party tank your target (or else any enemy in combat) is attacking;
    /// otherwise the first tank in the party. Can be you.
    /// </summary>
    public static (ulong Id, string Name)? Tank()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return null;
        var tanks = new System.Collections.Generic.List<Dalamud.Game.ClientState.Objects.Types.IGameObject>();
        foreach (var member in Plugin.PartyList)
            if (TankJobs.Contains(member.ClassJob.RowId) && Plugin.ObjectTable.SearchByEntityId(member.EntityId) is { } obj) tanks.Add(obj);
        if (tanks.Count == 0 && TankJobs.Contains(me.ClassJob.RowId)) tanks.Add(me);   // solo tank
        if (tanks.Count == 0) return null;

        ulong? held = null;
        if (Plugin.TargetManager.Target is Dalamud.Game.ClientState.Objects.Types.IBattleNpc t) held = t.TargetObjectId;
        if (held == null || tanks.All(x => x.GameObjectId != held))
            foreach (var o in Plugin.ObjectTable)
                if (o is Dalamud.Game.ClientState.Objects.Types.IBattleNpc n &&
                    (n.StatusFlags & Dalamud.Game.ClientState.Objects.Enums.StatusFlags.Hostile) != 0 &&
                    (n.StatusFlags & Dalamud.Game.ClientState.Objects.Enums.StatusFlags.InCombat) != 0 &&
                    tanks.Any(x => x.GameObjectId == n.TargetObjectId))
                { held = n.TargetObjectId; break; }
        var tank = tanks.FirstOrDefault(x => x.GameObjectId == held) ?? tanks[0];
        return (tank.GameObjectId, tank.Name.TextValue);
    }

    /// <summary>The other tank in the party, or null if there isn't one.</summary>
    public static (ulong Id, string Name)? CoTank()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return null;
        foreach (var member in Plugin.PartyList)
        {
            if (member.EntityId == me.EntityId || !TankJobs.Contains(member.ClassJob.RowId)) continue;
            if (Plugin.ObjectTable.SearchByEntityId(member.EntityId) is { } obj) return (obj.GameObjectId, obj.Name.TextValue);
        }
        return null;
    }
}
