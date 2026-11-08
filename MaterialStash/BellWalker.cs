using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using MaterialStash.Core;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MaterialStash;

/// <summary>
/// To the nearest summoning bell: one in sight is walked to (vnavmesh) and rung; with none in sight,
/// Lifestream goes to the market board ("/li mb"), where cities keep bells close by [Assumption], and
/// the nearest one there is used. Stops at the bell's retainer list.
/// </summary>
public sealed unsafe class BellWalker : IDisposable
{
    private enum Phase { Idle, Travel, Walk, Ring }

    private readonly VNavIpc vnav = new();
    private readonly LifestreamIpc lifestream = new();
    private Phase phase = Phase.Idle;
    private DateTime next, phaseStart, lastProgress;
    private float best;
    private int tries;
    private bool travelAsked;

    public string Status { get; private set; } = "";
    public bool Busy => phase != Phase.Idle;

    public BellWalker() => Plugin.Framework.Update += OnUpdate;

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        if (Busy) vnav.Stop();
    }

    private static IGameObject? NearestBell()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return null;
        return Plugin.ObjectTable
            .Where(o => o.IsTargetable && o.ObjectKind is ObjectKind.EventObj or ObjectKind.HousingEventObject &&
                        o.Name.TextValue.Equals("Summoning Bell", StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => Vector3.Distance(o.Position, me.Position))
            .FirstOrDefault();
    }

    public void Start()
    {
        if (Busy) { Stop(); return; }
        if (GameUi.IsOpen("RetainerList")) { Status = "Already at a bell."; return; }
        if (Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.BoundByDuty]) { Status = "Not in combat or a duty."; return; }
        travelAsked = false;
        if (NearestBell() != null) Set(Phase.Walk, "Walking to the summoning bell...");
        else if (lifestream.IsLoaded) Set(Phase.Travel, "No bell in sight: going to the market board (Lifestream)...");
        else { Status = "No summoning bell in sight (Lifestream would take you to one)."; Plugin.ChatGui.Print($"[Stash] {Status}"); }
    }

    public void Stop()
    {
        if (!Busy) return;
        vnav.Stop();
        lifestream.Abort();
        phase = Phase.Idle;
        Status = "Stopped.";
    }

    private void Set(Phase p, string status)
    {
        phase = p; Status = status; tries = 0;
        phaseStart = lastProgress = DateTime.UtcNow;
        best = float.MaxValue;
    }

    private void Fail(string why)
    {
        vnav.Stop();
        phase = Phase.Idle;
        Status = why;
        Plugin.ChatGui.PrintError($"[Stash] {why}");
    }

    private void OnUpdate(IFramework framework)
    {
        if (phase == Phase.Idle || DateTime.UtcNow < next) return;
        next = DateTime.UtcNow.AddSeconds(0.4);
        try
        {
            if (Plugin.Condition[ConditionFlag.InCombat]) { Fail("Combat: stopped."); return; }
            if (GameUi.IsOpen("RetainerList")) { vnav.Stop(); phase = Phase.Idle; Status = "At the bell."; return; }
            if ((DateTime.UtcNow - phaseStart).TotalSeconds > (phase == Phase.Travel ? 240 : 60)) { Fail($"Timed out ({phase})."); return; }
            switch (phase)
            {
                case Phase.Travel: TickTravel(); break;
                case Phase.Walk: TickWalk(); break;
                case Phase.Ring: TickRing(); break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Material Stash: bell walk failed");
            Fail("Error (see /xllog).");
        }
    }

    private void TickTravel()
    {
        if (!travelAsked)
        {
            if (!lifestream.Execute("mb")) { Fail("Lifestream didn't take the request."); return; }
            travelAsked = true;
            next = DateTime.UtcNow.AddSeconds(2);
            return;
        }
        if (lifestream.IsBusy || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.ObjectTable.LocalPlayer == null) return;
        if (NearestBell() != null) { Set(Phase.Walk, "Walking to the summoning bell..."); return; }
        if ((DateTime.UtcNow - phaseStart).TotalSeconds > 30) Fail("No summoning bell near the market board.");
    }

    private void TickWalk()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        var bell = NearestBell();
        if (me == null) return;
        if (bell == null) { Fail("Lost sight of the summoning bell."); return; }
        var dist = Vector3.Distance(me.Position, bell.Position);
        if (dist <= 3f) { vnav.Stop(); Set(Phase.Ring, "Ringing the bell..."); return; }
        if (!vnav.IsLoaded) { Fail("Walk to the bell yourself (vnavmesh isn't installed)."); return; }
        if (dist < best - 0.5f) { best = dist; lastProgress = DateTime.UtcNow; }
        if ((DateTime.UtcNow - lastProgress).TotalSeconds > 12) { Fail("vnavmesh isn't getting closer to the bell."); return; }
        if (!vnav.IsReady || vnav.IsBusy) return;
        vnav.MoveCloseTo(vnav.NearestPoint(bell.Position, 3, 5) ?? bell.Position, 2f);
    }

    private void TickRing()
    {
        if (Plugin.Condition[ConditionFlag.OccupiedSummoningBell]) return; // the list is on its way
        if (++tries > 4) { Fail("The bell didn't answer."); return; }
        var bell = NearestBell();
        if (bell == null) { Fail("Lost sight of the summoning bell."); return; }
        Plugin.TargetManager.Target = bell;
        TargetSystem.Instance()->InteractWithObject((GameObjectStruct*)bell.Address, true);
        next = DateTime.UtcNow.AddSeconds(2);
    }
}
