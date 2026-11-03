using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace MitPlanner;

/// <summary>
/// Runs the current fight's timeline clock and keeps it synced to what the boss actually does,
/// the way cactbot does: each "sync" line matches an ability ID within a time window, and when
/// that ability is seen the clock snaps to the line's time (or its jump target).
/// </summary>
public sealed unsafe class TimelineRunner : IDisposable
{
    private readonly Hook<ActionEffectHandler.Delegates.Receive>? actionEffectHook;
    private readonly Stopwatch clock = new();
    private float clockBase;            // timeline time when the stopwatch was last reset
    private DateTime outOfCombatSince = DateTime.MinValue;
    private readonly HashSet<(uint Entity, uint Action)> castsSeen = new();

    public FightDefinition? Fight { get; private set; }
    public Timeline? Timeline { get; private set; }
    public bool Running => clock.IsRunning;
    public bool Preview { get; private set; }
    public float Now => clockBase + (float)clock.Elapsed.TotalSeconds;
    public string LastSync { get; private set; } = "none";

    /// <summary>Entity that last used a timeline sync ability: the boss, for aggro checks.</summary>
    public uint BossEntityId { get; private set; }

    /// <summary>Raised when a new pull starts (plans reset their "already prompted" state).</summary>
    public event Action? PullStarted;

    /// <summary>Raised when a sync sends the clock backwards (fight loops); arg = new time.</summary>
    public event Action<float>? JumpedBack;

    public TimelineRunner()
    {
        try
        {
            actionEffectHook = Plugin.GameInterop.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
                ActionEffectHandler.MemberFunctionPointers.Receive, OnActionEffect);
            actionEffectHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Couldn't hook action effects; timeline will only sync on cast bars.");
        }

        Plugin.Framework.Update += OnUpdate;
        Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
        OnTerritoryChanged(Plugin.ClientState.TerritoryType);
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        actionEffectHook?.Dispose();
    }

    // ================================================================ control

    public void StartPreview(FightDefinition fight)
    {
        Fight = fight;
        Timeline = Fights.Load(fight);
        Preview = true;
        Restart(0);
        PullStarted?.Invoke();
    }

    public void Stop()
    {
        clock.Reset();
        clockBase = 0;
        castsSeen.Clear();
        BossEntityId = 0;
        if (Preview)
        {
            Preview = false;
            OnTerritoryChanged(Plugin.ClientState.TerritoryType);
        }
    }

    private void Restart(float at)
    {
        clockBase = at;
        lastTick = at;
        clock.Restart();
    }

    private float lastTick;

    /// <summary>"forcejump" lines jump the moment the clock reaches them, whether or not they synced.</summary>
    private void CheckForceJumps()
    {
        var now = Now;
        var from = lastTick;
        lastTick = now;
        foreach (var e in Timeline!.Events)
        {
            if (!e.ForceJump || e.Jump is not { } target || e.Time <= from || e.Time > now) continue;
            LastSync = $"{e.Label} forcejump {e.Time:0.0}s -> {target:0.0}s";
            Restart(target + (now - e.Time));
            if (target < e.Time - 1) JumpedBack?.Invoke(target);
            return;
        }
    }

    private void OnTerritoryChanged(uint territory)
    {
        if (Preview) return;
        Stop();
        Fight = Fights.ForTerritory(territory);
        Timeline = Fight != null ? Fights.Load(Fight) : null;
    }

    // ================================================================ per frame

    private void OnUpdate(IFramework framework)
    {
        if (Timeline == null) return;
        if (Running) CheckForceJumps();
        if (Preview) return;

        var inCombat = Plugin.Condition[ConditionFlag.InCombat];
        if (inCombat)
        {
            outOfCombatSince = DateTime.MinValue;
            if (!Running)
            {
                Restart(0);
                LastSync = "combat start";
                PullStarted?.Invoke();
            }
            PollCastBars();
        }
        else if (Running)
        {
            // Wipe, kill, or phase-change cutscene. Phase starts re-sync via their wide windows.
            if (outOfCombatSince == DateTime.MinValue) outOfCombatSince = DateTime.UtcNow;
            else if ((DateTime.UtcNow - outOfCombatSince).TotalSeconds > 3) Stop();
        }
    }

    /// <summary>"StartsUsing" syncs: watch enemy cast bars for newly started casts.</summary>
    private void PollCastBars()
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.ObjectKind != ObjectKind.BattleNpc || obj is not IBattleChara npc) continue;
            if (!npc.IsCasting) { castsSeen.RemoveWhere(c => c.Entity == npc.EntityId); continue; }

            if (castsSeen.Add((npc.EntityId, npc.CastActionId)))
                TrySync(npc.CastActionId, SyncKind.StartsUsing, npc.EntityId);
        }
    }

    /// <summary>"Ability" syncs: every resolved action arrives here.</summary>
    private void OnActionEffect(uint casterEntityId, Character* casterPtr, Vector3* targetPos,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        actionEffectHook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);
        try
        {
            if (Timeline == null || !Running || Preview || header == null) return;
            if (Plugin.ObjectTable.SearchByEntityId(casterEntityId) is not { ObjectKind: ObjectKind.BattleNpc }) return;
            TrySync(header->ActionId, SyncKind.Ability, casterEntityId);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Timeline sync failed");
        }
    }

    private void TrySync(uint actionId, SyncKind kind, uint casterEntityId)
    {
        if (Timeline == null || !Running) return;

        var id = actionId.ToString("X");
        var now = Now;
        TimelineEvent? best = null;
        foreach (var e in Timeline.Events)
        {
            if (e.Sync != kind || e.IdPattern == null) continue;
            if (now < e.Time - e.WindowBefore || now > e.Time + e.WindowAfter) continue;
            if (!e.IdPattern.IsMatch(id)) continue;
            if (best == null || Math.Abs(e.Time - now) < Math.Abs(best.Time - now)) best = e;
        }
        if (best == null) return;

        var target = best.Jump ?? best.Time;
        LastSync = $"{best.Label} ({id}) {now:0.0}s -> {target:0.0}s";
        UpdateBoss(casterEntityId);
        Restart(target);
        if (target < now - 1) JumpedBack?.Invoke(target);
    }

    /// <summary>Adds (mines, bits, images) also sync; the boss is the sync caster with the most max HP.</summary>
    private void UpdateBoss(uint casterEntityId)
    {
        if (casterEntityId == BossEntityId) return;
        if (Plugin.ObjectTable.SearchByEntityId(casterEntityId) is not IBattleChara caster) return;
        if (BossEntityId == 0 || Plugin.ObjectTable.SearchByEntityId(BossEntityId) is not IBattleChara current ||
            caster.MaxHp >= current.MaxHp)
            BossEntityId = casterEntityId;
    }

    // ================================================================ queries

    /// <summary>The next visible mechanics, including any that landed in the last second.</summary>
    public IEnumerable<TimelineEvent> Upcoming(int count, Func<TimelineEvent, bool>? filter = null)
    {
        if (Timeline == null) return Enumerable.Empty<TimelineEvent>();
        var now = Running ? Now : 0;
        return Timeline.Events
            .Where(e => !e.Hidden && e.Time >= now - 1 && (filter == null || filter(e)))
            .Take(count);
    }

    public TimelineEvent? FindByKey(string key) => Timeline?.Events.FirstOrDefault(e => e.Key == key);
}
