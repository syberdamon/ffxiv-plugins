using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Network;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace MitPlanner;

/// <summary>Who a mechanic is going to hit, and how we know.</summary>
public enum TargetSource
{
    Unknown,
    HeadMarker,   // a marker cactbot associates with this mechanic is on the player
    CastTarget,   // the boss's cast bar for this ability names the player
    Aggro,        // nothing more specific yet: whoever the boss is attacking
}

public readonly record struct TargetCheck(bool IsMe, TargetSource Source)
{
    public string Describe() => Source switch
    {
        TargetSource.HeadMarker => "marker",
        TargetSource.CastTarget => "cast",
        TargetSource.Aggro => "aggro",
        _ => "unknown",
    };
}

/// <summary>
/// Answers "is this mechanic targeting me?" from head markers, cast targets, and aggro,
/// most reliable first.
/// </summary>
public sealed unsafe class Targeting : IDisposable
{
    // ActorControl category for head markers (lock-on icons). [Assumption] 0x22, as used by
    // cactbot/ACT's network parsing; Shadowbringers-era marker IDs are not offset.
    private const uint HeadMarkerCategory = 0x22;
    private const double MarkerLifetimeSeconds = 10;

    private readonly Hook<PacketDispatcher.Delegates.HandleActorControlPacket>? actorControlHook;
    private readonly TimelineRunner runner;
    private readonly List<(uint Entity, uint Marker, DateTime At)> markers = new();

    public Targeting(TimelineRunner runner)
    {
        this.runner = runner;
        runner.PullStarted += markers.Clear;
        try
        {
            actorControlHook = Plugin.GameInterop.HookFromAddress<PacketDispatcher.Delegates.HandleActorControlPacket>(
                PacketDispatcher.MemberFunctionPointers.HandleActorControlPacket, OnActorControl);
            actorControlHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Couldn't hook actor control; head-marker targeting disabled (cast/aggro still work).");
        }
    }

    public void Dispose()
    {
        runner.PullStarted -= markers.Clear;
        actorControlHook?.Dispose();
    }

    private void OnActorControl(uint entityId, uint category, uint arg1, uint arg2, uint arg3, uint arg4,
        uint arg5, uint arg6, uint arg7, uint arg8, GameObjectId targetId, bool isRecorded)
    {
        actorControlHook!.Original(entityId, category, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, targetId, isRecorded);
        try
        {
            if (category != HeadMarkerCategory) return;
            markers.RemoveAll(m => (DateTime.UtcNow - m.At).TotalSeconds > MarkerLifetimeSeconds);
            markers.Add((entityId, arg1, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Head marker tracking failed");
        }
    }

    public TargetCheck Check(FightDefinition fight, TimelineEvent ev)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return new(false, TargetSource.Unknown);

        // 1. Head marker for this mechanic, seen recently.
        if (fight.HeadMarkers != null && fight.HeadMarkers.TryGetValue(ev.Label, out var markerId))
        {
            var recent = markers.Where(m => m.Marker == markerId && (DateTime.UtcNow - m.At).TotalSeconds <= MarkerLifetimeSeconds).ToList();
            if (recent.Count > 0) return new(recent.Any(m => m.Entity == me.EntityId), TargetSource.HeadMarker);
        }

        // 2. A boss currently casting this ability at a player.
        if (ev.IdPattern != null)
        {
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj.ObjectKind != ObjectKind.BattleNpc || obj is not IBattleChara npc || !npc.IsCasting) continue;
                if (!ev.IdPattern.IsMatch(npc.CastActionId.ToString("X"))) continue;

                var target = Plugin.ObjectTable.SearchById(npc.CastTargetObjectId);
                if (target is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter)
                    return new(target.EntityId == me.EntityId, TargetSource.CastTarget);
                // Cast on itself / the ground (e.g. a cleave): fall through to aggro.
            }
        }

        // 3. Aggro: whoever the boss is attacking right now.
        if (runner.BossEntityId != 0 && Plugin.ObjectTable.SearchByEntityId(runner.BossEntityId) is { } boss)
            return new(boss.TargetObjectId == me.GameObjectId, TargetSource.Aggro);

        return new(false, TargetSource.Unknown);
    }
}
