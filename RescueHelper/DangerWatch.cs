using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Network;

namespace RescueHelper;

/// <summary>One party member as the panel shows them.</summary>
public sealed record MemberState(ulong ObjectId, string Name, string Job, float Distance, bool InDanger, float Seconds,
    double DangerFor, string? Skip);

/// <summary>
/// Danger comes from BossMod's AI hints over IPC: "BossMod.Hints.IsDashSafe(from, to)" checks a spot
/// against every forbidden zone BossMod knows for this fight (and the arena edge), and
/// "BossMod.Hints.ForbiddenZonesNextActivation" says when the next one goes off. The zones are worked
/// out from YOUR point of view, so players whose own mechanic makes their spot "unsafe" (spread,
/// stack markers) are skipped, as are tanks holding enemies.
/// </summary>
public sealed unsafe class DangerWatch : IDisposable
{
    public const uint RescueId = 7571;
    private const float RescueRange = 30f;   // [Assumption] Rescue's range, yalms
    private const uint HeadMarkerCategory = 0x22;   // ActorControl category for head markers (as in Mit Planner)
    private static readonly HashSet<uint> HealerJobs = new() { 6, 24, 28, 33, 40 };
    private static readonly HashSet<uint> TankJobs = new() { 1, 3, 19, 21, 32, 37 };

    private readonly Configuration config;
    private readonly ICallGateSubscriber<bool> hasModule;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool> isDashSafe;
    private readonly ICallGateSubscriber<Vector3, bool> isPositionSafe;
    private readonly ICallGateSubscriber<float> nextActivation;
    private readonly ICallGateSubscriber<int> zoneCount;
    private readonly Hook<PacketDispatcher.Delegates.HandleActorControlPacket>? markerHook;
    private readonly Dictionary<uint, DateTime> markers = new();     // entity id -> marker time
    private readonly Dictionary<ulong, DateTime> dangerSince = new();
    private DateTime nextTick = DateTime.MinValue;
    private DateTime lastAuto = DateTime.MinValue;

    public List<MemberState> Members { get; private set; } = new();
    public string Status { get; private set; } = "";
    public bool MySpotSafe { get; private set; } = true;
    public bool RescueReady { get; private set; }
    public bool IsHealer { get; private set; }
    public string LastAction { get; private set; } = "None";

    public DangerWatch(Configuration config)
    {
        this.config = config;
        var pi = Plugin.PluginInterface;
        hasModule = pi.GetIpcSubscriber<bool>("BossMod.HasActiveModule");
        isDashSafe = pi.GetIpcSubscriber<Vector3, Vector3, bool>("BossMod.Hints.IsDashSafe");
        isPositionSafe = pi.GetIpcSubscriber<Vector3, bool>("BossMod.Hints.IsPositionSafe");
        nextActivation = pi.GetIpcSubscriber<float>("BossMod.Hints.ForbiddenZonesNextActivation");
        zoneCount = pi.GetIpcSubscriber<int>("BossMod.Hints.ForbiddenZonesCount");
        Plugin.Framework.Update += OnUpdate;
        try
        {
            markerHook = Plugin.GameInterop.HookFromAddress<PacketDispatcher.Delegates.HandleActorControlPacket>(
                PacketDispatcher.MemberFunctionPointers.HandleActorControlPacket, OnActorControl);
            markerHook.Enable();
        }
        catch (Exception ex) { Plugin.Log.Error(ex, "Rescue Helper: no head-marker tracking (marked players won't be skipped)"); }
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        markerHook?.Dispose();
    }

    private void OnActorControl(uint entityId, uint category, uint arg1, uint arg2, uint arg3, uint arg4,
        uint arg5, uint arg6, uint arg7, uint arg8, GameObjectId targetId, bool isRecorded)
    {
        markerHook!.Original(entityId, category, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, targetId, isRecorded);
        if (category == HeadMarkerCategory) markers[entityId] = DateTime.UtcNow;
    }

    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < nextTick) return;
        nextTick = DateTime.UtcNow.AddMilliseconds(100);
        try { Tick(); }
        catch (Exception ex) { Status = "Error (see /xllog)"; Plugin.Log.Error(ex, "Rescue Helper tick failed"); }
    }

    private void Tick()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        Members = new();
        IsHealer = me != null && HealerJobs.Contains(me.ClassJob.RowId);
        if (me == null || !config.Enabled) { Status = config.Enabled ? "" : "Off"; return; }
        if (!IsHealer) { Status = "Not on a healer"; return; }
        if (!Plugin.Condition[ConditionFlag.InCombat]) { Status = "Out of combat"; dangerSince.Clear(); return; }

        bool module;
        try { module = hasModule.InvokeFunc(); }
        catch { Status = "BossMod isn't running (it supplies the danger zones)"; return; }
        if (!module) { Status = "BossMod has no module for this fight"; return; }

        var zones = Try(() => zoneCount.InvokeFunc(), 0);
        var next = zones == 0 ? float.MaxValue : Try(() => nextActivation.InvokeFunc(), float.MaxValue);
        MySpotSafe = Try(() => isPositionSafe.InvokeFunc(me.Position), true);
        Status = zones == 0 ? "No danger zones right now" : $"{zones} danger zone(s), next in {(next > 99 ? "?" : $"{next:0.0}s")}";

        var am = ActionManager.Instance();
        var now = DateTime.UtcNow;
        foreach (var id in markers.Where(m => (now - m.Value).TotalSeconds > config.MarkerMemorySeconds).Select(m => m.Key).ToList())
            markers.Remove(id);

        var held = new HashSet<ulong>(Plugin.ObjectTable.OfType<IBattleNpc>()
            .Where(n => (n.StatusFlags & StatusFlags.Hostile) != 0 && (n.StatusFlags & StatusFlags.InCombat) != 0)
            .Select(n => n.TargetObjectId));

        var list = new List<MemberState>();
        var seen = new HashSet<ulong>();
        foreach (var pm in Plugin.PartyList)
        {
            if (pm.GameObject is not IPlayerCharacter pc || pc.GameObjectId == me.GameObjectId) continue;
            seen.Add(pc.GameObjectId);
            var dist = Vector3.Distance(pc.Position, me.Position);
            var job = pc.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? "?";
            string? skip = null;
            if (pc.CurrentHp == 0) skip = "dead";
            else if (config.IgnoreTanksHoldingEnemies && TankJobs.Contains(pc.ClassJob.RowId) && held.Contains(pc.GameObjectId)) skip = "tanking";
            else if (config.IgnoreMarkedPlayers && markers.ContainsKey(pc.EntityId)) skip = "has a marker";

            var unsafeSpot = skip == null && zones > 0 && !Try(() => isDashSafe.InvokeFunc(pc.Position, pc.Position), true);
            var danger = unsafeSpot && next <= config.WarnSeconds;
            if (danger) { if (!dangerSince.ContainsKey(pc.GameObjectId)) dangerSince[pc.GameObjectId] = now; }
            else dangerSince.Remove(pc.GameObjectId);
            var forMs = danger ? (now - dangerSince[pc.GameObjectId]).TotalMilliseconds : 0;
            list.Add(new MemberState(pc.GameObjectId, pc.Name.TextValue, job, dist, danger, danger ? next : 0, forMs, skip));
        }
        foreach (var k in dangerSince.Keys.Where(k => !seen.Contains(k)).ToList()) dangerSince.Remove(k);
        Members = list;

        RescueReady = am != null && am->GetActionStatus(ActionType.Action, RescueId) == 0;

        if (config.Mode == RescueMode.Automatic && (now - lastAuto).TotalSeconds > config.AutoCooldownSeconds)
        {
            var pick = list.Where(m => m.InDanger && m.DangerFor >= config.AutoDelayMs && m.Seconds <= config.AutoWithinSeconds)
                .OrderBy(m => m.Seconds).FirstOrDefault();
            if (pick != null && TryRescue(pick, automatic: true)) lastAuto = now;
        }
    }

    /// <summary>Rescue this member, if it's safe and possible. Returns false with LastAction saying why not.</summary>
    public bool TryRescue(MemberState m, bool automatic = false)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        var am = ActionManager.Instance();
        string? why = null;
        if (me == null || am == null) why = "not ready";
        else if (!MySpotSafe) why = "your own spot is in a danger zone";
        else if (m.Distance > RescueRange) why = $"out of range ({m.Distance:0}y)";
        else if (am->GetActionStatus(ActionType.Action, RescueId, m.ObjectId) != 0) why = "Rescue isn't ready";
        if (why != null)
        {
            if (!automatic) Note($"Can't rescue {m.Name}: {why}");
            return false;
        }
        if (!am->UseAction(ActionType.Action, RescueId, m.ObjectId)) { if (!automatic) Note($"Rescue on {m.Name} didn't go out"); return false; }
        Note($"Rescued {m.Name} ({m.Job}){(automatic ? " automatically" : "")}, AoE in {m.Seconds:0.0}s");
        return true;
    }

    private void Note(string text)
    {
        LastAction = $"{DateTime.Now:HH:mm:ss} {text}";
        if (config.ChatFeedback) Plugin.ChatGui.Print("[Rescue] " + text);
    }

    private static T Try<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }
}
