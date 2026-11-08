using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MaterialStash.Windows;

/// <summary>
/// A small movable bar: how many retainers are ready (venture done, or none assigned) and how long
/// until the next one is. From the game's retainer list (after the bell has been used once this
/// session), else AutoRetainer's own count for this character (works right after login).
/// Click it to open Material Stash.
/// </summary>
public sealed unsafe class VentureWindow : Window
{
    private static readonly Vector4 Ready = new(0.45f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 Dim = new(0.7f, 0.7f, 0.7f, 1f);

    private readonly Plugin plugin;
    private readonly ICallGateSubscriber<ulong, long?> arNext;
    private DateTime nextPoll = DateTime.MinValue;
    private int ready = -1, out_;
    private long? secondsToNext;

    public VentureWindow(Plugin plugin) : base("Ventures###MaterialStashVentures")
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        IsOpen = true;
        arNext = Plugin.PluginInterface.GetIpcSubscriber<ulong, long?>("AutoRetainer.PluginState.GetClosestRetainerVentureSecondsRemaining");
    }

    public override bool DrawConditions()
    {
        var c = plugin.Configuration;
        if (!c.ShowVentureWindow || Tracker.ContentId() == 0) return false;
        if (c.VentureWindowAlways) return true;
        var cond = Plugin.Condition;
        return !cond[ConditionFlag.InCombat] && !cond[ConditionFlag.BoundByDuty] && !cond[ConditionFlag.BoundByDuty56] &&
               !cond[ConditionFlag.OccupiedInCutSceneEvent] && !cond[ConditionFlag.WatchingCutscene] && !cond[ConditionFlag.WatchingCutscene78] &&
               !cond[ConditionFlag.BetweenAreas];
    }

    public override void PreDraw()
    {
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoFocusOnAppearing;
        ShowCloseButton = false;
        ImGui.SetNextWindowPos(new Vector2(ImGui.GetMainViewport().Size.X - 260, 120), ImGuiCond.FirstUseEver);
    }

    private void Poll()
    {
        if (DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow.AddSeconds(1);
        ready = -1; out_ = 0; secondsToNext = null;
        var rm = RetainerManager.Instance();
        if (rm != null && rm->IsReady && rm->GetRetainerCount() > 0)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ready = 0;
            long? soonest = null;
            for (uint i = 0; i < rm->GetRetainerCount(); i++)
            {
                var r = rm->GetRetainerBySortedIndex(i);
                if (r == null || r->RetainerId == 0) continue;
                if (r->VentureId == 0 || r->VentureComplete <= now) { ready++; continue; }
                out_++;
                var left = r->VentureComplete - now;
                if (soonest == null || left < soonest) soonest = left;
            }
            secondsToNext = soonest;
        }
        else
        {
            try { secondsToNext = arNext.InvokeFunc(Tracker.ContentId()); } catch (Exception) { secondsToNext = null; }
            if (secondsToNext is <= 0) { ready = 1; secondsToNext = null; } // AutoRetainer: at least one ready
        }
    }

    public override void Draw()
    {
        Poll();
        string text;
        Vector4 color;
        if (ready > 0)
        {
            text = $"Ventures: {ready} ready" + (secondsToNext is { } s ? $" · next in {Minutes(s)}" : "");
            color = Ready;
        }
        else if (secondsToNext is { } s2)
        {
            text = $"Ventures: next in {Minutes(s2)}";
            color = Dim;
        }
        else
        {
            text = ready == 0 && out_ == 0 ? "Ventures: none out" : "Ventures: open a bell once";
            color = Dim;
        }
        if (plugin.Bell.Busy) { text = "Going to the bell... (click to stop)"; color = Dim; }
        if (plugin.IceBreak.Active) { text = $"ICE break: {plugin.IceBreak.Status}"; color = Ready; }
        else if (plugin.Configuration.IceBreakEnabled) text += " · ICE break on";
        ImGui.TextColored(color, text);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !plugin.IceBreak.Active) plugin.Bell.Start();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) plugin.ToggleMain();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Left-click: go to the nearest summoning bell (walks to one in sight; otherwise Lifestream to the market board first).\n" +
                             "Right-click: Material Stash. Hide or pin this on the Retainers tab." +
                             (plugin.Bell.Status.Length > 0 && !plugin.Bell.Busy ? $"\n{plugin.Bell.Status}" : ""));
    }

    private static string Minutes(long seconds) =>
        seconds >= 3600 ? $"{seconds / 3600}h {(seconds % 3600) / 60:00}m" : $"{Math.Max(1, (seconds + 59) / 60)} min";
}
