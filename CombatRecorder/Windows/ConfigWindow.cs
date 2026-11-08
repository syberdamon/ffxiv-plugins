using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CombatRecorder.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin) : base("Combat Recorder###CombatRecorderConfig")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 220), MaximumSize = new Vector2(900, 900) };
    }

    public override void Draw()
    {
        var c = plugin.Configuration;
        var r = plugin.Recorder;
        var changed = false;

        var enabled = c.Enabled;
        if (ImGui.Checkbox("Record", ref enabled)) { c.Enabled = enabled; changed = true; if (!enabled) r.Close("turned off"); }
        var outside = c.RecordOutsideDuties;
        if (ImGui.Checkbox("Also record outside duties (open world, FATEs, hunts)", ref outside)) { c.RecordOutsideDuties = outside; changed = true; }
        var chat = c.ChatNotices;
        if (ImGui.Checkbox("Chat line when a recording starts/ends", ref chat)) { c.ChatNotices = chat; changed = true; }
        var snap = c.SnapshotSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderFloat("HP/position snapshot every (s)", ref snap, 0.2f, 2f, "%.1f")) { c.SnapshotSeconds = snap; changed = true; }
        var max = c.MaxFiles;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Keep at most (files)", ref max, 10, 50)) { c.MaxFiles = System.Math.Max(10, max); changed = true; }
        if (changed) c.Save();

        ImGui.Separator();
        ImGui.TextUnformatted(r.CurrentFile is { } f ? $"Recording: {Path.GetFileName(f)} ({r.EventsWritten} events)" : "Not recording right now.");
        ImGui.TextDisabled($"Files: {r.Folder}");
        ImGui.TextWrapped("Mark a moment for review with /crec mark <note> (put it on a macro), e.g. /crec mark downtime here. " +
                          "Party members are saved by job and slot only. Nothing is uploaded.");
        if (r.CurrentFile != null && ImGui.Button("Close current file now")) r.Close("closed from settings");

        ImGui.Separator();
        ImGui.TextUnformatted("DPS meter");
        var me = c.MeterEnabled;
        if (ImGui.Checkbox("Count damage and healing", ref me)) { c.MeterEnabled = me; c.Save(); }
        var sm = c.ShowMeter;
        if (ImGui.Checkbox("Show the meter window (/crec meter)", ref sm)) { c.ShowMeter = sm; c.Save(); }
        var cp = c.MeterCompact;
        if (ImGui.Checkbox("Compact view (bars only)", ref cp)) { c.MeterCompact = cp; c.Save(); }
        var oc = c.MeterOnlyInCombat;
        if (ImGui.Checkbox("Only while fighting", ref oc)) { c.MeterOnlyInCombat = oc; c.Save(); }
        var ev = c.MeterEveryone;
        if (ImGui.Checkbox("Count everyone, not just my party (open world)", ref ev)) { c.MeterEveryone = ev; c.Save(); }
        var hn = c.MeterHideNames;
        if (ImGui.Checkbox("Hide names (show jobs)", ref hn)) { c.MeterHideNames = hn; c.Save(); }
        var op = c.MeterOpacity;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderFloat("Background opacity", ref op, 0f, 1f, "%.2f")) { c.MeterOpacity = op; c.Save(); }
        ImGui.TextDisabled("Counted like ACT: encounter DPS = damage / time from first to last hit, DoTs and pets included.");
    }
}
