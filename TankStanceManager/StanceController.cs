using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace TankStanceManager;

/// <summary>
/// Reads the enmity list for your current target and presses tank stance
/// according to the configured role.
/// </summary>
public sealed unsafe class StanceController : IDisposable
{
    // ClassJob row id -> (stance action id, stance status id).
    // [Assumption] IDs are from memory (Dawntrail-era). Verify with the
    // "Stance detected" readout in the settings window; correct here if wrong.
    private static readonly Dictionary<uint, (uint ActionId, uint StatusId, string Name)> Stances = new()
    {
        { 1,  (28,    79,   "Iron Will")   }, // Gladiator
        { 19, (28,    79,   "Iron Will")   }, // Paladin
        { 3,  (48,    91,   "Defiance")    }, // Marauder
        { 21, (48,    91,   "Defiance")    }, // Warrior
        { 32, (3629,  743,  "Grit")        }, // Dark Knight
        { 37, (16142, 1833, "Royal Guard") }, // Gunbreaker
    };

    private readonly Configuration config;
    private DateTime lastToggle = DateTime.MinValue;

    // Live state, shown in the settings window for verification.
    public string StatusText { get; private set; } = "Idle";
    public int MyRank { get; private set; }
    public int MyPercent { get; private set; }
    public bool? StanceOn { get; private set; }
    public string LastAction { get; private set; } = "None";

    public StanceController(Configuration config)
    {
        this.config = config;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    private void OnUpdate(IFramework framework)
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            StatusText = "Error (see /xllog)";
            Plugin.Log.Error(ex, "TankStanceManager tick failed");
        }
    }

    private void Tick()
    {
        MyRank = 0;
        MyPercent = 0;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) { StanceOn = null; StatusText = "Not logged in"; return; }

        var jobId = player.ClassJob.RowId;
        if (!Stances.TryGetValue(jobId, out var stance)) { StanceOn = null; StatusText = "Not on a tank job"; return; }

        StanceOn = player.StatusList.Any(s => s.StatusId == stance.StatusId);

        if (!config.Enabled) { StatusText = "Disabled"; return; }

        // MT mode: stance on at all times (in or out of combat, any zone).
        // Re-applies after death, job swaps, or anything else that drops it.
        if (config.Role == TankRole.MainTank)
        {
            if (StanceOn == false) TryToggle(stance, wantOn: true, "MT mode: stance must be on");
            else StatusText = "MT: stance on";
            return;
        }

        // ----- OT mode -----
        if (!Plugin.Condition[ConditionFlag.InCombat]) { StatusText = "Out of combat"; return; }
        if (config.DutiesOnly && !Plugin.Condition[ConditionFlag.BoundByDuty] && !Plugin.Condition[ConditionFlag.BoundByDuty56])
        { StatusText = "Not in a duty"; return; }
        if (config.RequireCoTank && !HasCoTank()) { StatusText = "No other tank in party"; return; }

        // UIState.Hate is the enmity list the game shows for your current target.
        var hate = &UIState.Instance()->Hate;
        if (hate->HateTargetId == 0 || hate->HateArrayLength <= 0) { StatusText = "Target an enemy to read enmity"; return; }

        var entries = new List<(uint Id, int Enmity)>(hate->HateArrayLength);
        for (var i = 0; i < hate->HateArrayLength && i < 32; i++)
            entries.Add((hate->HateInfo[i].EntityId, hate->HateInfo[i].Enmity));
        entries.Sort((a, b) => b.Enmity.CompareTo(a.Enmity));

        var myIndex = entries.FindIndex(e => e.Id == player.EntityId);
        if (myIndex < 0) { StatusText = "You're not on this target's enmity list"; return; }

        var top = entries[0].Enmity;
        MyRank = myIndex + 1;
        // Values are already relative (top = 100), but normalize in case they aren't.
        MyPercent = top > 0 ? (int)Math.Round(entries[myIndex].Enmity * 100.0 / top) : 0;

        // Safety: OT should never hold #1 with stance on.
        if (MyRank == 1)
        {
            if (StanceOn == true) TryToggle(stance, wantOn: false, "OT is #1: dropping stance");
            else StatusText = "OT is #1 with stance off (MT needs to Provoke)";
            return;
        }

        var low = Math.Min(config.LowThreshold, config.HighThreshold);
        var high = Math.Max(config.LowThreshold, config.HighThreshold);
        var belowBand = MyPercent < low || (config.TreatRankThreeAsLow && MyRank >= 3);

        if (StanceOn == false && belowBand)
            TryToggle(stance, wantOn: true, $"OT at {MyPercent}% (#{MyRank}): below band");
        else if (StanceOn == true && MyPercent >= high)
            TryToggle(stance, wantOn: false, $"OT at {MyPercent}%: reached {high}%");
        else
            StatusText = $"OT holding: #{MyRank} at {MyPercent}% (band {low}-{high}%)";
    }

    private void TryToggle((uint ActionId, uint StatusId, string Name) stance, bool wantOn, string reason)
    {
        StatusText = reason;

        if ((DateTime.UtcNow - lastToggle).TotalMilliseconds < config.MinToggleIntervalMs) return;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || player.IsCasting) return;

        var am = ActionManager.Instance();
        if (am == null || am->AnimationLock > 0) return;

        // When stance is on, the game swaps the button to "Release <stance>".
        // GetAdjustedActionId returns whichever version is currently live.
        var actionId = am->GetAdjustedActionId(stance.ActionId);
        if (am->GetActionStatus(ActionType.Action, actionId) != 0) return; // not usable right now

        if (am->UseAction(ActionType.Action, actionId))
        {
            lastToggle = DateTime.UtcNow;
            LastAction = $"{DateTime.Now:HH:mm:ss} {(wantOn ? "ON" : "OFF")} - {reason}";
            if (config.ChatFeedback)
                Plugin.ChatGui.Print($"[TSM] {stance.Name} {(wantOn ? "ON" : "OFF")} ({reason})");
        }
    }

    private static bool HasCoTank()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        foreach (var member in Plugin.PartyList)
        {
            if (me != null && member.EntityId == me.EntityId) continue;
            if (Stances.ContainsKey(member.ClassJob.RowId)) return true;
        }
        return false;
    }
}
