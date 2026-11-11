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

public readonly record struct TargetCheck(bool IsMe, TargetSource Source, ulong TargetObjectId = 0)
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
    private readonly Configuration config;
    private readonly List<(uint Entity, uint Marker, DateTime At)> markers = new();

    /// <summary>A head marker appeared: (entity id, marker id).</summary>
    public event Action<uint, uint>? MarkerPlaced;

    public Targeting(TimelineRunner runner, Configuration config)
    {
        this.runner = runner;
        this.config = config;
        runner.PullStarted += markers.Clear;
        runner.AbilityResolved += OnAbilityResolved;
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
        runner.AbilityResolved -= OnAbilityResolved;
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
            MarkerPlaced?.Invoke(entityId, arg1);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Head marker tracking failed");
        }
    }

    /// <summary>
    /// Learn which timeline line a head-marker mechanic is: when an enemy ability hits a player who
    /// had a marker cactbot calls a tank buster (or stack) in the last 10 s, that line gets the tag.
    /// Only fills lines that are untagged or plain "mechanic"; your own tags always win.
    /// </summary>
    private void OnAbilityResolved(uint actionId, uint[] targets)
    {
        var fight = runner.Fight;
        if (fight?.MarkerKinds == null || fight.MarkerKinds.Count == 0 || runner.Preview || targets.Length == 0) return;

        var recent = markers.Where(m => (DateTime.UtcNow - m.At).TotalSeconds <= MarkerLifetimeSeconds &&
                                        targets.Contains(m.Entity) && fight.MarkerKinds.ContainsKey(m.Marker)).ToList();
        if (recent.Count == 0) return;
        var kind = recent.Select(m => fight.MarkerKinds[m.Marker]).OrderBy(k => k.Rank()).First();

        var ev = runner.EventForAbility(actionId);
        if (ev == null) return;
        var key = Configuration.TagKey(fight.TerritoryId, ev.Label);
        if (config.TagOverrides.ContainsKey(key) || config.LearnedTags.ContainsKey(key)) return;
        var current = config.TagFor(fight, ev.Label);
        if (current is not (MechanicType.Other or MechanicType.Mechanic)) return;

        config.LearnedTags[key] = kind;
        config.Save();
        // Markers that fired with this ability now count as "this mechanic targets them" too.
        markers.RemoveAll(m => recent.Contains(m));
        Plugin.ChatGui.Print($"[MitPlanner] Learned: {ev.Label} = {Windows.Style.TypeNames[(int)kind].ToLowerInvariant()} (cactbot head marker). Change it in the Plan tab if that's wrong.");

        // Slot mits for it into your plan for the next pull (around what's already planned).
        if (kind.IsBuster() && config.AutoPlanLearned && runner.Timeline != null && Plugin.ObjectTable.LocalPlayer is { } me)
        {
            var added = PlanGenerator.AddForLearnedBuster(config, fight, runner.Timeline, me.ClassJob.RowId,
                ActionCatalog.ForFight(fight), ev.Label);
            if (added.Count > 0)
                Plugin.ChatGui.Print($"[MitPlanner] Added to your {Configuration.PlanRoleCode(me.ClassJob.RowId, config.Role)} plan: {string.Join(", ", added)}");
        }
    }

    /// <summary>The other tank in the party (fallback target for OT Sent Mits).</summary>
    public static ulong CoTankObjectId()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return 0;
        foreach (var member in Plugin.PartyList)
        {
            if (member.EntityId == me.EntityId) continue;
            if (TankKits.For(member.ClassJob.RowId) == null) continue;
            if (Plugin.ObjectTable.SearchByEntityId(member.EntityId) is { } obj) return obj.GameObjectId;
        }
        return 0;
    }

    public TargetCheck Check(FightDefinition fight, TimelineEvent ev)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return new(false, TargetSource.Unknown);

        // 1. Head marker for this mechanic, seen recently.
        if (fight.HeadMarkers != null && fight.HeadMarkers.TryGetValue(ev.Label, out var markerId))
        {
            var recent = markers.Where(m => m.Marker == markerId && (DateTime.UtcNow - m.At).TotalSeconds <= MarkerLifetimeSeconds).ToList();
            if (recent.Count > 0)
            {
                var isMe = recent.Any(m => m.Entity == me.EntityId);
                var other = recent.Select(m => Plugin.ObjectTable.SearchByEntityId(m.Entity))
                                  .FirstOrDefault(o => o != null && o.EntityId != me.EntityId);
                return new(isMe, TargetSource.HeadMarker, isMe ? me.GameObjectId : other?.GameObjectId ?? 0);
            }
        }

        // 1b. Any recent head marker cactbot uses for this mechanic's type (e.g. its tank-buster marker).
        if (fight.MarkerKinds is { Count: > 0 } kinds)
        {
            var type = config.TagFor(fight, ev.Label);
            var recent = markers.Where(m => kinds.TryGetValue(m.Marker, out var k) && k == type &&
                                            (DateTime.UtcNow - m.At).TotalSeconds <= MarkerLifetimeSeconds).ToList();
            if (recent.Count > 0 && (type.IsBuster() || type == MechanicType.Stack))
            {
                var isMe = recent.Any(m => m.Entity == me.EntityId);
                var other = recent.Select(m => Plugin.ObjectTable.SearchByEntityId(m.Entity))
                                  .FirstOrDefault(o => o != null && o.EntityId != me.EntityId);
                return new(isMe, TargetSource.HeadMarker, isMe ? me.GameObjectId : other?.GameObjectId ?? 0);
            }
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
                    return new(target.EntityId == me.EntityId, TargetSource.CastTarget, target.GameObjectId);
                // Cast on itself / the ground (e.g. a cleave): fall through to aggro.
            }
        }

        // 3. Aggro: whoever the boss is attacking right now.
        if (runner.BossEntityId != 0 && Plugin.ObjectTable.SearchByEntityId(runner.BossEntityId) is { } boss)
            return new(boss.TargetObjectId == me.GameObjectId, TargetSource.Aggro, boss.TargetObjectId);

        return new(false, TargetSource.Unknown);
    }
}
