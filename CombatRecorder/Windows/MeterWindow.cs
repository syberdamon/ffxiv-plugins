using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;

namespace CombatRecorder.Windows;

/// <summary>
/// The DPS meter: the current (or a past) encounter, one row per player. Full view: DPS, rDPS, aDPS, share, damage,
/// crit %, direct hit %, deaths, HPS, damage taken (each column can be hidden); click a row for that
/// player's skills. Compact view: one bar per player, with an optional % share. A small Full/Bars button
/// swaps views; right-click anywhere on the meter for views, past fights and settings. Position mode
/// shows it at any time (sample rows if there's no fight) so it can be moved and resized out of combat.
/// </summary>
public sealed class MeterWindow : Window
{
    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Mine = new(1f, 0.85f, 0.4f, 1f);
    private static readonly Vector4 MoveColor = new(1f, 0.8f, 0.2f, 1f);
    private static readonly Vector4 MyBar = new(0.85f, 0.65f, 0.2f, 0.85f);
    private static readonly Vector4 OtherBar = new(0.3f, 0.5f, 0.8f, 0.85f);
    private static readonly Vector4 TankBar = new(0.25f, 0.45f, 0.85f, 0.85f);
    private static readonly Vector4 HealerBar = new(0.25f, 0.65f, 0.3f, 0.85f);
    private static readonly Vector4 DpsBar = new(0.75f, 0.25f, 0.25f, 0.85f);
    private static readonly HashSet<string> Tanks = new() { "PLD", "WAR", "DRK", "GNB", "GLA", "MRD" };
    private static readonly HashSet<string> Healers = new() { "WHM", "SCH", "AST", "SGE", "CNJ" };

    private static readonly Vector2 DefaultPos = new(40, 420);
    private static readonly Vector2 DefaultSize = new(520, 220);

    private enum Col { Dps, Rdps, Adps, Share, Damage, Crit, Dh, Deaths, Hps, Taken }

    private static readonly (Col Col, string Header, float Width)[] Columns =
    {
        (Col.Dps, "DPS", 60), (Col.Rdps, "rDPS", 60), (Col.Adps, "aDPS", 60), (Col.Share, "%", 38), (Col.Damage, "Damage", 72), (Col.Crit, "Crit", 38),
        (Col.Dh, "DH", 38), (Col.Deaths, "Deaths", 44), (Col.Hps, "HPS", 55), (Col.Taken, "Taken", 66),
    };

    private readonly Plugin plugin;
    private int shown = -1; // -1 = current, else index into History
    private uint expanded;
    private bool resetLayout;
    private int pushedStyles;
    private Encounter? sample;
    private bool moving;
    private bool? sizedFor;      // the view (compact or not) whose saved size was last applied
    private int tableGen;        // bumped to make ImGui forget column widths (reset)

    /// <summary>Default compact size: the old bar width, about six rows tall.</summary>
    private Vector2 CompactDefault => new(Math.Clamp(C.MeterBarWidth, 120, 1000) + 16, 170);

    public MeterWindow(Plugin plugin) : base("Meter###CombatRecorderMeter")
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        IsOpen = true;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(160, 40), MaximumSize = new Vector2(1600, 1400) };
    }

    private Configuration C => plugin.Configuration;

    /// <summary>Position mode: always visible, unlocked, sample rows when there's no fight. Not saved.</summary>
    public bool Moving
    {
        get => moving;
        set { moving = value; if (!value) sample = null; }
    }

    /// <summary>Put the meter back at its default place and size (next frame).</summary>
    public void ResetLayout() => resetLayout = true;

    public override bool DrawConditions()
    {
        if (Plugin.ObjectTable.LocalPlayer == null) return false;
        if (Moving) return true;
        if (!C.MeterEnabled || !C.ShowMeter) return false;
        var cond = Plugin.Condition;
        if (C.MeterHideInCutscenes &&
            (cond[ConditionFlag.OccupiedInCutSceneEvent] || cond[ConditionFlag.WatchingCutscene] || cond[ConditionFlag.WatchingCutscene78]))
            return false;
        if (!C.MeterOnlyInCombat || cond[ConditionFlag.InCombat]) return true;
        var cur = plugin.Meter.Current;
        if (cur is { End: null }) return true;
        return cur?.End is { } end && (DateTime.UtcNow - end).TotalSeconds < C.MeterLingerSeconds;
    }

    public override void PreDraw()
    {
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing;
        if (!Moving && C.MeterLocked)
        {
            Flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
            if (C.MeterClickThrough) Flags |= ImGuiWindowFlags.NoInputs;
        }
        BgAlpha = Moving ? Math.Max(C.MeterOpacity, 0.85f) : C.MeterOpacity;
        ShowCloseButton = false;

        if (resetLayout)
        {
            ImGui.SetNextWindowPos(DefaultPos, ImGuiCond.Always);
            var size = C.MeterCompact ? CompactDefault : DefaultSize;
            ImGui.SetNextWindowSize(size, ImGuiCond.Always);
            StoreSize(size);
            sizedFor = C.MeterCompact;
            resetLayout = false;
        }
        else if (sizedFor != C.MeterCompact)
        {
            // First draw, or the view changed: use that view's own saved size.
            var saved = C.MeterCompact ? new Vector2(C.MeterCompactWidth, C.MeterCompactHeight) : new Vector2(C.MeterFullWidth, C.MeterFullHeight);
            if (saved.X <= 0 || saved.Y <= 0) saved = C.MeterCompact ? CompactDefault : DefaultSize;
            ImGui.SetNextWindowSize(saved, sizedFor == null ? ImGuiCond.FirstUseEver : ImGuiCond.Always);
            if (sizedFor == null) ImGui.SetNextWindowPos(DefaultPos, ImGuiCond.FirstUseEver);
            sizedFor = C.MeterCompact;
        }
        else
        {
            ImGui.SetNextWindowSize(DefaultSize, ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(DefaultPos, ImGuiCond.FirstUseEver);
        }

        pushedStyles = 0;
        if (Moving)
        {
            ImGui.PushStyleColor(ImGuiCol.Border, MoveColor);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2f);
            pushedStyles = 1;
        }
    }

    public override void PostDraw()
    {
        if (pushedStyles == 0) return;
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        pushedStyles = 0;
    }

    private void StoreSize(Vector2 size)
    {
        if (C.MeterCompact) { C.MeterCompactWidth = size.X; C.MeterCompactHeight = size.Y; }
        else { C.MeterFullWidth = size.X; C.MeterFullHeight = size.Y; }
    }

    /// <summary>Remember the window's size for the current view once a drag-resize is finished.</summary>
    private void TrackSize()
    {
        if (sizedFor != C.MeterCompact || ImGui.IsMouseDown(ImGuiMouseButton.Left)) return;
        var size = ImGui.GetWindowSize();
        var old = C.MeterCompact ? new Vector2(C.MeterCompactWidth, C.MeterCompactHeight) : new Vector2(C.MeterFullWidth, C.MeterFullHeight);
        if (Math.Abs(size.X - old.X) < 1 && Math.Abs(size.Y - old.Y) < 1) return;
        StoreSize(size);
        C.Save();
    }

    public override void Draw()
    {
        TrackSize();
        ImGui.SetWindowFontScale(Math.Clamp(C.MeterFontScale, 0.5f, 2.5f));
        var m = plugin.Meter;
        if (shown >= m.History.Count) shown = -1;
        var e = shown < 0 ? m.Current : m.History[shown];

        if (Moving)
        {
            ImGui.TextColored(MoveColor, "Position mode: drag to move, drag the corner to resize.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Done")) Moving = false;
            if (e == null || e.Players.Count == 0) e = sample ??= Sample();
        }

        // ---- right-click anywhere: views, past fights, settings
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("##metermenu");
        if (ImGui.BeginPopup("##metermenu"))
        {
            Menu(m);
            ImGui.EndPopup();
        }

        // ---- view swap button (compact <-> full), leads the title line; hidden when clicks pass through
        var swap = !(C.MeterLocked && C.MeterClickThrough && !Moving);
        if (swap)
        {
            if (ImGui.SmallButton(C.MeterCompact ? "Full##swap" : "Bars##swap")) { C.MeterCompact = !C.MeterCompact; C.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(C.MeterCompact ? "Switch to the full table view." : "Switch to the compact bar view.");
            if (C.MeterShowTitle || Moving) ImGui.SameLine();
        }

        // ---- title line
        if (C.MeterShowTitle || Moving)
        {
            var title = e == null ? "Meter: no fight yet" :
                $"{e.Title}  {Clock(e.Seconds)}  {e.TotalDamage / e.Seconds:N0} dps" +
                (e == sample ? "  (sample)" : e.End == null ? "" : shown >= 0 ? $"  (past #{shown + 1})" : "  (ended)");
            ImGui.TextColored(Dim, title);
            if (ImGui.IsItemHovered() && !Moving) ImGui.SetTooltip("Right-click: views, past fights, position mode, settings.");
        }
        if (e == null || e.Players.Count == 0) return;

        var rows = Rows(e);
        var secs = e.Seconds;
        var total = Math.Max(1, e.TotalDamage);

        if (C.MeterCompact)
        {
            var top = Math.Max(1, rows.Max(SortValue));
            // share of everyone counted (not just the rows shown, so max rows doesn't inflate it)
            var sum = Math.Max(1, e.Players.Values.Sum(SortValue));
            var cscale = Math.Clamp(C.MeterFontScale, 0.5f, 2.5f);

            // Number columns, laid out from the right edge with the full view's widths (so resizing a column there
            // resizes it here too). Name takes what's left on the left.
            var ccols = new List<(string Header, float Width, Func<Combatant, string> Value)>();
            switch (C.MeterSortBy)
            {
                case 1: ccols.Add(("HPS", ColWidth("HPS", 55), p => $"{p.Healing / secs:N0}")); break;
                case 2: ccols.Add(("Taken", ColWidth("Taken", 66), p => $"{p.Taken:N0}")); break;
                default:
                    ccols.Add(("DPS", ColWidth("DPS", 60), p => $"{p.Damage / secs:N0}"));
                    if (C.MeterColRdps) ccols.Add(("rDPS", ColWidth("rDPS", 60), p => $"{p.RaidDamage / secs:N0}"));
                    break;
            }
            if (C.MeterCompactPercent) ccols.Add(("%", ColWidth("%", 38), p => $"{100.0 * SortValue(p) / sum:0}%"));

            var width = Math.Max(40, ImGui.GetContentRegionAvail().X);
            var pad = ImGui.GetStyle().FramePadding;
            var gap = ImGui.GetStyle().ItemSpacing.X;
            // left edge of each number column, relative to the bar's left
            var lefts = new float[ccols.Count];
            var right = width - pad.X;
            for (var i = ccols.Count - 1; i >= 0; i--) { right -= ccols[i].Width * cscale; lefts[i] = right; right -= gap; }
            var nameWidth = Math.Max(0, right - pad.X);

            void Line(Vector2 origin, float height, string name, Func<int, string> value, Vector4? color)
            {
                if (color is { } col) ImGui.PushStyleColor(ImGuiCol.Text, col);
                ImGui.SetCursorScreenPos(origin + new Vector2(pad.X, (height - ImGui.GetTextLineHeight()) / 2));
                ImGui.TextUnformatted(Fit(name, nameWidth));
                for (var i = 0; i < ccols.Count; i++)
                {
                    var t = value(i);
                    var w = ccols[i].Width * cscale;
                    var free = Math.Max(0, w - ImGui.CalcTextSize(t).X);
                    var x = C.MeterAlign switch { 0 => 0, 1 => free / 2, _ => free };
                    ImGui.SetCursorScreenPos(origin + new Vector2(lefts[i] + x, (height - ImGui.GetTextLineHeight()) / 2));
                    ImGui.TextUnformatted(t);
                }
                if (color != null) ImGui.PopStyleColor();
                ImGui.SetCursorScreenPos(origin);
                ImGui.Dummy(new Vector2(width, height)); // one layout item for the whole line
            }

            // header line
            Line(ImGui.GetCursorScreenPos(), ImGui.GetTextLineHeightWithSpacing(), "Name", i => ccols[i].Header, Dim);

            foreach (var p in rows)
            {
                var origin = ImGui.GetCursorScreenPos();
                var h = ImGui.GetFrameHeight();
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, BarColor(p));
                ImGui.ProgressBar((float)SortValue(p) / top, new Vector2(width, h), "");
                ImGui.PopStyleColor();
                Line(origin, h, Label(p), i => ccols[i].Value(p), null);
            }
            return;
        }

        var cols = Columns.Where(c => Shown(c.Col)).ToList();
        var scale = Math.Clamp(C.MeterFontScale, 0.5f, 2.5f);
        // Drag the lines between headers to resize a column; widths are saved in the plugin's settings (not ImGui's),
        // so the table's ID carries the shown-column set and a reset counter.
        var mask = cols.Aggregate(0, (m, c) => m | (1 << (int)c.Col));
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY | ImGuiTableFlags.NoSavedSettings;
        if (!Moving) flags |= ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV;
        if (!ImGui.BeginTable($"##meter{mask}_{tableGen}", 1 + cols.Count, flags)) return;
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 1f);
        foreach (var c in cols)
            ImGui.TableSetupColumn(c.Header, ImGuiTableColumnFlags.WidthFixed,
                (C.MeterColumnWidths.TryGetValue(c.Header, out var w) && w > 8 ? w : c.Width) * scale);
        ImGui.TableSetupScrollFreeze(0, 1);

        // Header row, aligned like the numbers; also where the current widths are read back.
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        ImGui.TableHeader("Name");
        var widthsChanged = false;
        foreach (var c in cols)
        {
            ImGui.TableNextColumn();
            var w = ImGui.GetContentRegionAvail().X / scale;
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) &&
                (!C.MeterColumnWidths.TryGetValue(c.Header, out var old) ? Math.Abs(w - c.Width) >= 1 : Math.Abs(w - old) >= 1))
            {
                C.MeterColumnWidths[c.Header] = (float)Math.Round(w, 1);
                widthsChanged = true;
            }
            Align(c.Header);
            ImGui.TableHeader(c.Header);
        }
        if (widthsChanged) C.Save();
        foreach (var p in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var open = expanded == p.Id && !Moving;
            var label = $"{(open ? "v " : "> ")}{Label(p)}";
            if (C.MeterRoleColors) ImGui.PushStyleColor(ImGuiCol.Text, NameColor(p));
            if (Moving) ImGui.TextUnformatted(label); // plain text so dragging the rows moves the window
            else if (ImGui.Selectable($"{label}##{p.Id}", open, ImGuiSelectableFlags.SpanAllColumns)) expanded = open ? 0 : p.Id;
            if (C.MeterRoleColors) ImGui.PopStyleColor();
            if (!Moving && ImGui.IsItemHovered()) ImGui.SetTooltip($"Max hit: {p.MaxHit:N0} ({p.MaxHitName}). Click for skills.");
            foreach (var c in cols) Cell(PlayerCell(c.Col, p, secs, total), c.Col == Col.Dps && p.IsMe && C.MeterHighlightMe);
            if (!open) continue;
            foreach (var (skill, s) in p.Skills.OrderByDescending(k => k.Value.Damage).ThenByDescending(k => k.Value.Healing))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextColored(Dim, $"    {skill}");
                foreach (var c in cols) Cell(SkillCell(c.Col, p, s, secs), false, true);
            }
        }
        ImGui.EndTable();
    }

    private void Menu(Meter m)
    {
        if (ImGui.MenuItem("Position mode (move / resize)", "", Moving)) Moving = !Moving;
        if (ImGui.MenuItem("Compact view", "", C.MeterCompact)) { C.MeterCompact = !C.MeterCompact; C.Save(); }
        if (C.MeterCompact && ImGui.MenuItem("Show % in compact view", "", C.MeterCompactPercent)) { C.MeterCompactPercent = !C.MeterCompactPercent; C.Save(); }
        if (ImGui.MenuItem("Lock position and size", "", C.MeterLocked)) { C.MeterLocked = !C.MeterLocked; C.Save(); }
        if (ImGui.BeginMenu("Sort by"))
        {
            if (ImGui.MenuItem("Damage", "", C.MeterSortBy == 0)) { C.MeterSortBy = 0; C.Save(); }
            if (ImGui.MenuItem("Healing", "", C.MeterSortBy == 1)) { C.MeterSortBy = 1; C.Save(); }
            if (ImGui.MenuItem("Damage taken", "", C.MeterSortBy == 2)) { C.MeterSortBy = 2; C.Save(); }
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("Columns"))
        {
            foreach (var (col, header, _) in Columns)
                if (ImGui.MenuItem(header, "", Shown(col))) { Toggle(col); C.Save(); }
            ImGui.Separator();
            if (ImGui.BeginMenu("Align numbers"))
            {
                for (var a = 0; a < AlignNames.Length; a++)
                    if (ImGui.MenuItem(AlignNames[a], "", C.MeterAlign == a)) { C.MeterAlign = a; C.Save(); }
                ImGui.EndMenu();
            }
            if (ImGui.MenuItem("Reset column widths")) ResetColumnWidths();
            ImGui.EndMenu();
        }
        if (ImGui.MenuItem("Hide names (show jobs)", "", C.MeterHideNames)) { C.MeterHideNames = !C.MeterHideNames; C.Save(); }
        if (ImGui.MenuItem("Color by role", "", C.MeterRoleColors)) { C.MeterRoleColors = !C.MeterRoleColors; C.Save(); }
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
    }

    // ---------------------------------------------------------------- rows

    private long SortValue(Combatant p) => C.MeterSortBy switch { 1 => p.Healing, 2 => p.Taken, _ => p.Damage };

    private List<Combatant> Rows(Encounter e)
    {
        var rows = e.Players.Values.OrderByDescending(SortValue).ThenByDescending(p => p.Damage).ToList();
        if (C.MeterMaxRows <= 0 || rows.Count <= C.MeterMaxRows) return rows;
        var top = rows.Take(C.MeterMaxRows).ToList();
        var me = rows.FirstOrDefault(p => p.IsMe);
        if (me != null && !top.Contains(me)) top[^1] = me;
        return top;
    }

    private static string PlayerCell(Col c, Combatant p, double secs, long total) => c switch
    {
        Col.Dps => $"{p.Damage / secs:N0}",
        Col.Rdps => $"{p.RaidDamage / secs:N0}",
        Col.Adps => $"{p.AdjustedDamage / secs:N0}",
        Col.Share => $"{100.0 * p.Damage / total:0}%",
        Col.Damage => $"{p.Damage:N0}",
        Col.Crit => p.Hits == 0 ? "-" : $"{100.0 * p.Crits / p.Hits:0}%",
        Col.Dh => p.Hits == 0 ? "-" : $"{100.0 * p.DirectHits / p.Hits:0}%",
        Col.Deaths => p.Deaths.ToString(),
        Col.Hps => $"{p.Healing / secs:N0}",
        Col.Taken => $"{p.Taken:N0}",
        _ => "",
    };

    /// <summary>Skill rows reuse the columns: Deaths shows hit count, Taken shows the max hit.</summary>
    private static string SkillCell(Col c, Combatant p, SkillTotals s, double secs) => c switch
    {
        Col.Dps => s.Damage > 0 ? $"{s.Damage / secs:N0}" : "",
        Col.Share => p.Damage > 0 && s.Damage > 0 ? $"{100.0 * s.Damage / p.Damage:0}%" : "",
        Col.Damage => s.Damage > 0 ? $"{s.Damage:N0}" : $"heal {s.Healing:N0}",
        Col.Crit => s.Hits == 0 ? "" : $"{100.0 * s.Crits / s.Hits:0}%",
        Col.Dh => s.Hits == 0 ? "" : $"{100.0 * s.DirectHits / s.Hits:0}%",
        Col.Deaths => s.Hits == 0 ? "" : $"x{s.Hits}",
        Col.Hps => s.Healing > 0 ? $"{s.Healing / secs:N0}" : "",
        Col.Taken => s.Max > 0 ? $"max {s.Max:N0}" : "",
        _ => "",
    };

    private bool Shown(Col c) => c switch
    {
        Col.Dps => C.MeterColDps, Col.Rdps => C.MeterColRdps, Col.Adps => C.MeterColAdps, Col.Share => C.MeterColShare, Col.Damage => C.MeterColDamage, Col.Crit => C.MeterColCrit,
        Col.Dh => C.MeterColDh, Col.Deaths => C.MeterColDeaths, Col.Hps => C.MeterColHps, Col.Taken => C.MeterColTaken,
        _ => true,
    };

    private void Toggle(Col c)
    {
        switch (c)
        {
            case Col.Dps: C.MeterColDps = !C.MeterColDps; break;
            case Col.Rdps: C.MeterColRdps = !C.MeterColRdps; break;
            case Col.Adps: C.MeterColAdps = !C.MeterColAdps; break;
            case Col.Share: C.MeterColShare = !C.MeterColShare; break;
            case Col.Damage: C.MeterColDamage = !C.MeterColDamage; break;
            case Col.Crit: C.MeterColCrit = !C.MeterColCrit; break;
            case Col.Dh: C.MeterColDh = !C.MeterColDh; break;
            case Col.Deaths: C.MeterColDeaths = !C.MeterColDeaths; break;
            case Col.Hps: C.MeterColHps = !C.MeterColHps; break;
            case Col.Taken: C.MeterColTaken = !C.MeterColTaken; break;
        }
    }

    /// <summary>A column's width at text size 1: the saved width from the full view, else the default.</summary>
    private float ColWidth(string header, float fallback) =>
        C.MeterColumnWidths.TryGetValue(header, out var w) && w > 8 ? w : fallback;

    /// <summary>Shortens text with "..." to fit a width.</summary>
    private static string Fit(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width) return text;
        for (var n = text.Length - 1; n > 0; n--)
        {
            var t = text[..n].TrimEnd() + "...";
            if (ImGui.CalcTextSize(t).X <= width) return t;
        }
        return "";
    }

    public static readonly string[] AlignNames = { "Left", "Center", "Right" };

    /// <summary>Back to the default column widths.</summary>
    public void ResetColumnWidths()
    {
        C.MeterColumnWidths.Clear();
        C.Save();
        tableGen++;
    }

    /// <summary>Column names and on/off state, for the settings window.</summary>
    public IEnumerable<(string Header, bool On, System.Action Flip)> ColumnToggles() =>
        Columns.Select(c => (c.Header, Shown(c.Col), (System.Action)(() => Toggle(c.Col))));

    // ---------------------------------------------------------------- looks

    private static string Role(Combatant p) => Tanks.Contains(p.Job) ? "tank" : Healers.Contains(p.Job) ? "healer" : "dps";

    private Vector4 BarColor(Combatant p)
    {
        if (p.IsMe && C.MeterHighlightMe) return MyBar;
        if (!C.MeterRoleColors) return OtherBar;
        return Role(p) switch { "tank" => TankBar, "healer" => HealerBar, _ => DpsBar };
    }

    private Vector4 NameColor(Combatant p)
    {
        if (p.IsMe && C.MeterHighlightMe) return Mine;
        return Role(p) switch
        {
            "tank" => new Vector4(0.55f, 0.7f, 1f, 1f),
            "healer" => new Vector4(0.55f, 0.9f, 0.55f, 1f),
            _ => new Vector4(1f, 0.6f, 0.6f, 1f),
        };
    }

    private string Label(Combatant p) => C.MeterHideNames || p.Name.Length == 0 ? (p.IsMe ? $"{p.Job} (you)" : p.Job) : $"{p.Name} ({p.Job})";

    /// <summary>Moves the cursor so the text lands left, centered or right in the current cell (the align setting).</summary>
    private void Align(string text)
    {
        if (C.MeterAlign == 0) return;
        var free = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X;
        if (free <= 0) return;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (C.MeterAlign == 1 ? free / 2 : free));
    }

    private void Cell(string text, bool mine = false, bool dim = false)
    {
        ImGui.TableNextColumn();
        Align(text);
        if (mine) ImGui.TextColored(Mine, text);
        else if (dim) ImGui.TextColored(Dim, text);
        else ImGui.TextUnformatted(text);
    }

    private static string Clock(double s) => $"{(int)s / 60}:{(int)s % 60:00}";

    /// <summary>Made-up rows for position mode when there's no fight to show (sizes only, not real numbers).</summary>
    private static Encounter Sample()
    {
        var now = DateTime.UtcNow;
        var e = new Encounter { Start = now.AddSeconds(-185), LastDamage = now, End = now, Zone = "Sample fight" };
        var meJob = Plugin.ObjectTable.LocalPlayer?.ClassJob.ValueNullable?.Abbreviation.ExtractText() ?? "WAR";
        (string Job, long Dps, long Hps, long Taken, bool Me)[] party =
        {
            ("PCT", 21000, 0, 180000, false), ("SAM", 19500, 0, 210000, false), ("BRD", 16800, 600, 160000, false),
            (meJob, 15500, 1800, 900000, true), ("DRK", 14200, 2100, 1100000, false), ("SGE", 9800, 14500, 230000, false),
            ("WHM", 8900, 16000, 190000, false), ("RDM", 18700, 900, 200000, false),
        };
        uint id = 1;
        foreach (var (job, dps, hps, taken, me) in party)
        {
            var c = new Combatant
            {
                Id = id++, Job = job, IsMe = me, InParty = true, Name = me ? "You" : "",
                Damage = dps * 185, Healing = hps * 185, Taken = taken, Hits = 100, Crits = 25, DirectHits = 30,
            };
            e.Players[c.Id] = c;
        }
        e.Enemies[999] = ("Sample fight", e.Players.Values.Sum(p => p.Damage));
        return e;
    }
}
