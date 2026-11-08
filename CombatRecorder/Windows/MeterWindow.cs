using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;

namespace CombatRecorder.Windows;

/// <summary>
/// The DPS meter: the current (or a past) encounter, one row per player. Full view: DPS, share, damage,
/// crit %, direct hit %, deaths, HPS, damage taken; click a row for that player's skills. Compact view:
/// one bar per player. Right-click the title line to switch views, pick a past fight, or open settings.
/// </summary>
public sealed class MeterWindow : Window
{
    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Mine = new(1f, 0.85f, 0.4f, 1f);

    private readonly Plugin plugin;
    private int shown = -1; // -1 = current, else index into History
    private uint expanded;

    public MeterWindow(Plugin plugin) : base("Meter###CombatRecorderMeter")
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        IsOpen = true;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(220, 60), MaximumSize = new Vector2(1200, 1200) };
    }

    private Configuration C => plugin.Configuration;

    public override bool DrawConditions()
    {
        if (!C.MeterEnabled || !C.ShowMeter || Plugin.ObjectTable.LocalPlayer == null) return false;
        if (C.MeterOnlyInCombat && !Plugin.Condition[ConditionFlag.InCombat] && plugin.Meter.Current is not { End: null }) return false;
        return true;
    }

    public override void PreDraw()
    {
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing;
        if (C.MeterLocked) Flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        if (C.MeterCompact) Flags |= ImGuiWindowFlags.AlwaysAutoResize;
        BgAlpha = C.MeterOpacity;
        ShowCloseButton = false;
        ImGui.SetNextWindowSize(new Vector2(520, 220), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(40, 420), ImGuiCond.FirstUseEver);
    }

    public override void Draw()
    {
        var m = plugin.Meter;
        if (shown >= m.History.Count) shown = -1;
        var e = shown < 0 ? m.Current : m.History[shown];

        // ---- title line
        var title = e == null ? "Meter: no fight yet" :
            $"{e.Title}  {Clock(e.Seconds)}  {e.TotalDamage / e.Seconds:N0} dps" + (e.End == null ? "" : shown >= 0 ? $"  (past #{shown + 1})" : "  (ended)");
        ImGui.TextColored(Dim, title);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Right-click: views, past fights, settings.");
        if (ImGui.BeginPopupContextItem("##metermenu"))
        {
            if (ImGui.MenuItem("Compact view", "", C.MeterCompact)) { C.MeterCompact = !C.MeterCompact; C.Save(); }
            if (ImGui.MenuItem("Lock position and size", "", C.MeterLocked)) { C.MeterLocked = !C.MeterLocked; C.Save(); }
            if (ImGui.MenuItem("Hide names (show jobs)", "", C.MeterHideNames)) { C.MeterHideNames = !C.MeterHideNames; C.Save(); }
            if (ImGui.MenuItem("Count everyone, not just my party", "", C.MeterEveryone)) { C.MeterEveryone = !C.MeterEveryone; C.Save(); }
            ImGui.Separator();
            if (ImGui.MenuItem("Current fight", "", shown < 0)) shown = -1;
            for (var i = 0; i < m.History.Count; i++)
            {
                var h = m.History[i];
                if (ImGui.MenuItem($"{h.Start.ToLocalTime():HH:mm}  {h.Title}  {Clock(h.Seconds)}##h{i}", "", shown == i)) shown = i;
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Clear history")) { m.Reset(); shown = -1; }
            if (ImGui.MenuItem("Settings")) plugin.ToggleConfig();
            ImGui.EndPopup();
        }
        if (e == null || e.Players.Count == 0) return;

        var rows = e.Players.Values.OrderByDescending(p => p.Damage).ToList();
        var total = Math.Max(1, e.TotalDamage);
        var secs = e.Seconds;
        var top = Math.Max(1, rows[0].Damage);

        if (C.MeterCompact)
        {
            foreach (var p in rows)
            {
                var frac = (float)p.Damage / top;
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, p.IsMe ? new Vector4(0.85f, 0.65f, 0.2f, 0.85f) : new Vector4(0.3f, 0.5f, 0.8f, 0.85f));
                ImGui.ProgressBar(frac, new Vector2(240, 0), $"{Label(p)}  {p.Damage / secs:N0}");
                ImGui.PopStyleColor();
            }
            return;
        }

        if (!ImGui.BeginTable("##meter", 9, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY)) return;
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("DPS", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("%", ImGuiTableColumnFlags.WidthFixed, 38);
        ImGui.TableSetupColumn("Damage", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Crit", ImGuiTableColumnFlags.WidthFixed, 38);
        ImGui.TableSetupColumn("DH", ImGuiTableColumnFlags.WidthFixed, 38);
        ImGui.TableSetupColumn("Deaths", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("HPS", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Taken", ImGuiTableColumnFlags.WidthFixed, 66);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        foreach (var p in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var open = expanded == p.Id;
            if (ImGui.Selectable($"{(open ? "v " : "> ")}{Label(p)}##{p.Id}", open, ImGuiSelectableFlags.SpanAllColumns)) expanded = open ? 0 : p.Id;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Max hit: {p.MaxHit:N0} ({p.MaxHitName}). Click for skills.");
            Cell($"{p.Damage / secs:N0}", p.IsMe);
            Cell($"{100.0 * p.Damage / total:0}%");
            Cell($"{p.Damage:N0}");
            Cell(p.Hits == 0 ? "-" : $"{100.0 * p.Crits / p.Hits:0}%");
            Cell(p.Hits == 0 ? "-" : $"{100.0 * p.DirectHits / p.Hits:0}%");
            Cell(p.Deaths.ToString());
            Cell($"{p.Healing / secs:N0}");
            Cell($"{p.Taken:N0}");
            if (!open) continue;
            foreach (var (skill, s) in p.Skills.OrderByDescending(k => k.Value.Damage).ThenByDescending(k => k.Value.Healing))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextColored(Dim, $"    {skill}");
                Cell(s.Damage > 0 ? $"{s.Damage / secs:N0}" : "", false, true);
                Cell(p.Damage > 0 && s.Damage > 0 ? $"{100.0 * s.Damage / p.Damage:0}%" : "", false, true);
                Cell(s.Damage > 0 ? $"{s.Damage:N0}" : $"heal {s.Healing:N0}", false, true);
                Cell(s.Hits == 0 ? "" : $"{100.0 * s.Crits / s.Hits:0}%", false, true);
                Cell(s.Hits == 0 ? "" : $"{100.0 * s.DirectHits / s.Hits:0}%", false, true);
                Cell(s.Hits == 0 ? "" : $"x{s.Hits}", false, true);
                Cell(s.Healing > 0 ? $"{s.Healing / secs:N0}" : "", false, true);
                Cell(s.Max > 0 ? $"max {s.Max:N0}" : "", false, true);
            }
        }
        ImGui.EndTable();
    }

    private string Label(Combatant p) => C.MeterHideNames || p.Name.Length == 0 ? (p.IsMe ? $"{p.Job} (you)" : p.Job) : $"{p.Name} ({p.Job})";

    private static void Cell(string text, bool mine = false, bool dim = false)
    {
        ImGui.TableNextColumn();
        if (mine) ImGui.TextColored(Mine, text);
        else if (dim) ImGui.TextColored(Dim, text);
        else ImGui.TextUnformatted(text);
    }

    private static string Clock(double s) => $"{(int)s / 60}:{(int)s % 60:00}";
}
