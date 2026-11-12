using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace RescueHelper;

/// <summary>The party panel: one row per member; red rows are in danger; click a row to Rescue.</summary>
public sealed class PanelWindow : Window
{
    private static readonly Vector4 Red = new(1f, 0.3f, 0.3f, 1f);
    private static readonly Vector4 Dim = new(0.6f, 0.6f, 0.6f, 1f);
    private readonly Plugin plugin;

    public PanelWindow(Plugin plugin) : base("Rescue###RescuePanel")
    {
        this.plugin = plugin;
        IsOpen = true;
        RespectCloseHotkey = false;
        // Cog in the panel's title bar: opens the settings window.
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(2, 1),
            Click = _ => plugin.ToggleSettings(),
            ShowTooltip = () => ImGui.SetTooltip("Rescue Helper settings"),
        });
    }

    public override bool DrawConditions() =>
        plugin.Configuration.Enabled && plugin.Configuration.ShowPanel && plugin.Watch.IsHealer;

    public override void PreDraw()
    {
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoCollapse;
        if (plugin.Configuration.LockPanel) Flags |= ImGuiWindowFlags.NoMove;
    }

    public override void Draw()
    {
        var w = plugin.Watch;
        var c = plugin.Configuration;
        ImGui.TextColored(Dim, $"{(c.Mode == RescueMode.Click ? "Click a red row" : "Automatic")} | Rescue {(w.RescueReady ? "ready" : "not ready")}{(w.MySpotSafe ? "" : " | YOUR SPOT IS UNSAFE")}");
        ImGui.TextColored(Dim, w.Status);
        foreach (var m in w.Members)
        {
            var label = $"{m.Job,-3} {m.Name}";
            var tail = m.Skip != null ? $"  ({m.Skip})" : m.InDanger ? $"  IN AOE {m.Seconds:0.0}s" : "";
            if (m.InDanger) ImGui.PushStyleColor(ImGuiCol.Text, Red);
            else if (m.Skip != null) ImGui.PushStyleColor(ImGuiCol.Text, Dim);
            else ImGui.PushStyleColor(ImGuiCol.Text, Vector4.One);
            if (ImGui.Selectable($"{label}{tail}##{m.ObjectId}", m.InDanger, ImGuiSelectableFlags.None, new Vector2(260, 0)))
                w.TryRescue(m);
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{m.Distance:0} yalms away. Click to Rescue.");
        }
        if (w.Members.Count == 0) ImGui.TextColored(Dim, "(no party members)");
    }
}

public sealed class SettingsWindow : Window
{
    private readonly Plugin plugin;

    public SettingsWindow(Plugin plugin) : base("Rescue Helper - Settings###RescueSettings")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(440, 360), MaximumSize = new Vector2(900, 900) };
    }

    public override void Draw()
    {
        var c = plugin.Configuration;
        var changed = false;
        bool Box(string label, bool v, Action<bool> set) { var x = v; if (ImGui.Checkbox(label, ref x)) { set(x); changed = true; } return x; }

        Box("Enabled", c.Enabled, v => c.Enabled = v);
        var mode = (int)c.Mode;
        ImGui.TextUnformatted("Mode:");
        ImGui.SameLine(); if (ImGui.RadioButton("Click to rescue", ref mode, 0)) { c.Mode = RescueMode.Click; changed = true; }
        ImGui.SameLine(); if (ImGui.RadioButton("Automatic", ref mode, 1)) { c.Mode = RescueMode.Automatic; changed = true; }

        // Timers (Ctrl+click a slider to type an exact value).
        ImGui.Separator();
        ImGui.TextUnformatted("Timers");
        var warn = c.WarnSeconds;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Mark as in danger when the AoE lands within (s)", ref warn, 0.5f, 8, "%.1f")) { c.WarnSeconds = warn; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("A player turns red in the panel once the AoE they stand in goes off within this time.");

        var auto = c.Mode == RescueMode.Automatic;
        if (!auto) ImGui.BeginDisabled();
        var delay = c.AutoDelayMs;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Automatic: wait before rescuing (ms)", ref delay, 0, 3000)) { c.AutoDelayMs = delay; changed = true; }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("They must have been red at least this long: a chance to walk out on their own first.");
        var within = c.AutoWithinSeconds;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Automatic: only when the AoE lands within (s)", ref within, 0.3f, 6, "%.1f")) { c.AutoWithinSeconds = within; changed = true; }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Rescue only this close to the hit, so someone with time to move isn't pulled early.");
        var cd = c.AutoCooldownSeconds;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Automatic: at most one rescue every (s)", ref cd, 0.5f, 10, "%.1f")) { c.AutoCooldownSeconds = cd; changed = true; }
        if (!auto) ImGui.EndDisabled();
        if (!auto) ImGui.TextDisabled("(The Automatic timers apply once Mode is Automatic.)");

        var mem = c.MarkerMemorySeconds;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Treat a head marker as active for (s)", ref mem, 1, 20, "%.0f")) { c.MarkerMemorySeconds = mem; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("With 'skip players with a mechanic marker' on: how long after a spread/stack marker they're left alone.");
        ImGui.Separator();
        Box("Never pull a tank holding enemies", c.IgnoreTanksHoldingEnemies, v => c.IgnoreTanksHoldingEnemies = v);
        Box("Skip players with a mechanic marker (spread, stack...)", c.IgnoreMarkedPlayers, v => c.IgnoreMarkedPlayers = v);
        Box("Show the party panel", c.ShowPanel, v => c.ShowPanel = v);
        Box("Lock panel position", c.LockPanel, v => c.LockPanel = v);
        Box("Print rescues to chat", c.ChatFeedback, v => c.ChatFeedback = v);

        ImGui.Separator();
        ImGui.TextWrapped("Danger zones come from BossMod (Reborn), which must be running with a module for the fight. They're BossMod's view from your position, so players whose own mechanic puts them in an 'unsafe' spot are skipped. It never rescues while your own spot is in danger.");
        ImGui.TextDisabled($"Status: {plugin.Watch.Status}");
        ImGui.TextDisabled($"Last: {plugin.Watch.LastAction}");
        if (changed) c.Save();
    }
}
