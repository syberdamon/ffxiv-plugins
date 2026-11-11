using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace MitPlanner;

/// <summary>Where a press goes: a party member to cast on, or a spot for a ground-targeted heal.</summary>
public readonly record struct PressTarget(ulong ObjectId, Vector3? Location, string? Label, bool Unresolved = false)
{
    public static readonly PressTarget Default = new(0, null, null);
}

public static class Placement
{
    public static readonly string[] GroundNames = { "On the tank", "On me", "On the boss", "Arena center" };
    public static readonly EntryPlacement[] GroundValues = { EntryPlacement.OnTank, EntryPlacement.OnSelf, EntryPlacement.OnBoss, EntryPlacement.ArenaCenter };
    public static readonly string[] AllyNames = { "Game default", "On the tank", "On me" };
    public static readonly EntryPlacement[] AllyValues = { EntryPlacement.Default, EntryPlacement.OnTank, EntryPlacement.OnSelf };

    public static bool IsTank(ulong objectId) =>
        objectId != 0 && Plugin.ObjectTable.SearchById(objectId) is IPlayerCharacter pc && TankKits.For(pc.ClassJob.RowId) != null;

    /// <summary>
    /// The tank a mechanic is about: whoever it targets if that's a tank, else the tank the boss is
    /// attacking, else any tank in the party (not you, if you're one and another exists).
    /// </summary>
    public static IPlayerCharacter? Tank(TargetCheck? check, TimelineRunner runner)
    {
        if (check is { TargetObjectId: not 0 } c && IsTank(c.TargetObjectId))
            return Plugin.ObjectTable.SearchById(c.TargetObjectId) as IPlayerCharacter;
        if (runner.BossEntityId != 0 && Plugin.ObjectTable.SearchByEntityId(runner.BossEntityId) is { } boss && IsTank(boss.TargetObjectId))
            return Plugin.ObjectTable.SearchById(boss.TargetObjectId) as IPlayerCharacter;
        var me = Plugin.ObjectTable.LocalPlayer;
        var tanks = Plugin.PartyList
            .Where(m => TankKits.For(m.ClassJob.RowId) != null)
            .Select(m => Plugin.ObjectTable.SearchByEntityId(m.EntityId) as IPlayerCharacter)
            .Where(p => p != null)
            .ToList();
        return tanks.FirstOrDefault(p => p!.EntityId != me?.EntityId) ?? tanks.FirstOrDefault();
    }

    /// <summary>Resolve an entry's placement right now. Unresolved = it needs a spot nobody knows.</summary>
    public static PressTarget Resolve(PlanEntry entry, CatalogEntry? ability, TargetCheck? check, TimelineRunner runner, ArenaCenter arena)
    {
        var placement = entry.Placement;
        if (ability == null || placement == EntryPlacement.Default && !ability.IsGround) return PressTarget.Default;
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return PressTarget.Default;

        if (ability.IsGround)
        {
            switch (placement)
            {
                case EntryPlacement.OnTank:
                    return Tank(check, runner) is { } t ? new(0, t.Position, $"on {t.Name.TextValue}") : new(0, null, "on the tank (none found)", true);
                case EntryPlacement.OnBoss:
                    return runner.BossEntityId != 0 && Plugin.ObjectTable.SearchByEntityId(runner.BossEntityId) is { } b
                        ? new(0, b.Position, "on the boss") : new(0, null, "on the boss (not seen yet)", true);
                case EntryPlacement.ArenaCenter:
                    return arena.Get() is { } c ? new(0, c, $"at arena center ({arena.Source})") : new(0, null, "at arena center (unknown: save one)", true);
                default: // On me (and Default, which ground heals don't offer)
                    return new(0, me.Position, "on you");
            }
        }

        if (!ability.CanTargetAlly) return PressTarget.Default;
        return placement switch
        {
            EntryPlacement.OnTank => Tank(check, runner) is { } t ? new(t.GameObjectId, null, $"on {t.Name.TextValue}") : new(0, null, "on the tank (none found)", true),
            EntryPlacement.OnSelf => new(me.GameObjectId, null, "on you"),
            _ => PressTarget.Default,
        };
    }
}
