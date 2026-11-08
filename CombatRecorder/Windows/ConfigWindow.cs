using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CombatRecorder.Windows;

public sealed class ConfigWindow : Window
{
    private static readonly string[] SortNames = { "Damage", "Healing", "Damage taken" };
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
        var mw = plugin.MeterWindow;
        if (ImGui.Button(mw.Moving ? "Done positioning" : "Position mode (move / resize now)")) mw.Moving = !mw.Moving;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shows the meter right now, unlocked, with sample rows if there's no fight.\nAlso: /crec meter move, or right-click the meter.");
        ImGui.SameLine();
        if (ImGui.Button("Reset position")) mw.ResetLayout();

        if (ImGui.CollapsingHeader("When it shows", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var me = c.MeterEnabled;
            if (ImGui.Checkbox("Count damage and healing", ref me)) { c.MeterEnabled = me; c.Save(); }
            var sm = c.ShowMeter;
            if (ImGui.Checkbox("Show the meter window (/crec meter)", ref sm)) { c.ShowMeter = sm; c.Save(); }
            var oc = c.MeterOnlyInCombat;
            if (ImGui.Checkbox("Only while fighting", ref oc)) { c.MeterOnlyInCombat = oc; c.Save(); }
            if (c.MeterOnlyInCombat)
            {
                ImGui.Indent();
                var lg = c.MeterLingerSeconds;
                ImGui.SetNextItemWidth(120);
                if (ImGui.SliderInt("Keep showing after a fight (s)", ref lg, 0, 120)) { c.MeterLingerSeconds = lg; c.Save(); }
                ImGui.Unindent();
            }
            var cs = c.MeterHideInCutscenes;
            if (ImGui.Checkbox("Hide during cutscenes", ref cs)) { c.MeterHideInCutscenes = cs; c.Save(); }
            var ev = c.MeterEveryone;
            if (ImGui.Checkbox("Count everyone, not just my party (open world)", ref ev)) { c.MeterEveryone = ev; c.Save(); }
        }

        if (ImGui.CollapsingHeader("Layout and look"))
        {
            var lk = c.MeterLocked;
            if (ImGui.Checkbox("Lock position and size", ref lk)) { c.MeterLocked = lk; c.Save(); }
            if (c.MeterLocked)
            {
                ImGui.Indent();
                var ct = c.MeterClickThrough;
                if (ImGui.Checkbox("Clicks pass through to the game", ref ct)) { c.MeterClickThrough = ct; c.Save(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("The meter ignores the mouse (no right-click menu or skill breakdown).\nUse this window or /crec meter move to change it.");
                ImGui.Unindent();
            }
            var cp = c.MeterCompact;
            if (ImGui.Checkbox("Compact view (bars only)", ref cp)) { c.MeterCompact = cp; c.Save(); }
            if (c.MeterCompact)
            {
                ImGui.Indent();
                var pc = c.MeterCompactPercent;
                if (ImGui.Checkbox("Show % share on the bars", ref pc)) { c.MeterCompactPercent = pc; c.Save(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Each player's share of the party total for what you sort by (damage, healing or damage taken).");
                ImGui.Unindent();
            }
            var ti = c.MeterShowTitle;
            if (ImGui.Checkbox("Show the title line (fight, time, total DPS)", ref ti)) { c.MeterShowTitle = ti; c.Save(); }
            var hn = c.MeterHideNames;
            if (ImGui.Checkbox("Hide names (show jobs)", ref hn)) { c.MeterHideNames = hn; c.Save(); }
            var rc = c.MeterRoleColors;
            if (ImGui.Checkbox("Color by role (tank / healer / DPS)", ref rc)) { c.MeterRoleColors = rc; c.Save(); }
            var hm = c.MeterHighlightMe;
            if (ImGui.Checkbox("Highlight my row", ref hm)) { c.MeterHighlightMe = hm; c.Save(); }

            ImGui.SetNextItemWidth(160);
            var sort = c.MeterSortBy;
            if (ImGui.Combo("Sort by", ref sort, SortNames, SortNames.Length)) { c.MeterSortBy = sort; c.Save(); }
            var mr = c.MeterMaxRows;
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderInt("Max rows (0 = all, you're always shown)", ref mr, 0, 24)) { c.MeterMaxRows = mr; c.Save(); }
            var fs = c.MeterFontScale;
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderFloat("Text size", ref fs, 0.6f, 2f, "%.2f")) { c.MeterFontScale = fs; c.Save(); }
            var bw = c.MeterBarWidth;
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderInt("Compact bar width", ref bw, 120, 600)) { c.MeterBarWidth = bw; c.Save(); }
            var op = c.MeterOpacity;
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderFloat("Background opacity", ref op, 0f, 1f, "%.2f")) { c.MeterOpacity = op; c.Save(); }
        }

        if (ImGui.CollapsingHeader("Columns (full view)"))
        {
            var i = 0;
            foreach (var (header, on, flip) in mw.ColumnToggles().ToList())
            {
                var v = on;
                if (i++ % 4 != 0) ImGui.SameLine(0, 12);
                if (ImGui.Checkbox($"{header}##col", ref v)) { flip(); c.Save(); }
            }
            ImGui.TextDisabled("In a player's skill rows, the Deaths column shows hit count and Taken shows the max hit.");
        }
        ImGui.TextDisabled("Counted like ACT: encounter DPS = damage / time from first to last hit, DoTs and pets included.");
    }
}
