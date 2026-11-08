using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;

namespace CombatRecorder.Windows;

/// <summary>
/// The DPS meter: the current (or a past) encounter, one row per player. Full view: DPS, share, damage,
/// crit %, direct hit %, deaths, HPS, damage taken (each column can be hidden); click a row for that
/// player's skills. Compact view: one bar per player. Right-click anywhere on the meter for views, past
/// fights and settings. Position mode shows it at any time (sample rows if there's no fight) so it can
/// be moved and resized out of combat.
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

    private enum Col { Dps, Share, Damage, Crit, Dh, Deaths, Hps, Taken }

    private static readonly (Col Col, string Header, float Width)[] Columns =
    {
        (Col.Dps, "DPS", 60), (Col.Share, "%", 38), (Col.Damage, "Damage", 72), (Col.Crit, "Crit", 38),
        (Col.Dh, "DH", 38), (Col.Deaths, "Deaths", 44), (Col.Hps, "HPS", 55), (Col.Taken, "Taken", 66),
    };

    private readonly Plugin plugin;
    private int shown = -1; // -1 = current, else index into History
    private uint expanded;
    private bool resetLayout;
    private int pushedStyles;
    private Encounter? sample;
    private bool moving;

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
        if (C.MeterCompact) Flags |= ImGuiWindowFlags.AlwaysAutoResize;
        BgAlpha = Moving ? Math.Max(C.MeterOpacity, 0.85f) : C.MeterOpacity;
        ShowCloseButton = false;

        if (resetLayout)
        {
            ImGui.SetNextWindowPos(DefaultPos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(DefaultSize, ImGuiCond.Always);
            resetLayout = false;
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

    public override void Draw()
    {
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
            foreach (var p in rows)
            {
                var v = SortValue(p);
                var text = C.MeterSortBy switch
                {
                    1 => $"{Label(p)}  {v / secs:N0} hps",
                    2 => $"{Label(p)}  {v:N0} taken",
                    _ => $"{Label(p)}  {v / secs:N0}",
                };
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, BarColor(p));
                ImGui.ProgressBar((float)v / top, new Vector2(Math.Clamp(C.MeterBarWidth, 80, 1000), 0), text);
                ImGui.PopStyleColor();
            }
            return;
        }

        var cols = Columns.Where(c => Shown(c.Col)).ToList();
        if (!ImGui.BeginTable("##meter", 1 + cols.Count, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY)) return;
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 2f);
        foreach (var c in cols) ImGui.TableSetupColumn(c.Header, ImGuiTableColumnFlags.WidthFixed, c.Width * C.MeterFontScale);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
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
        Col.Dps => C.MeterColDps, Col.Share => C.MeterColShare, Col.Damage => C.MeterColDamage, Col.Crit => C.MeterColCrit,
        Col.Dh => C.MeterColDh, Col.Deaths => C.MeterColDeaths, Col.Hps => C.MeterColHps, Col.Taken => C.MeterColTaken,
        _ => true,
    };

    private void Toggle(Col c)
    {
        switch (c)
        {
            case Col.Dps: C.MeterColDps = !C.MeterColDps; break;
            case Col.Share: C.MeterColShare = !C.MeterColShare; break;
            case Col.Damage: C.MeterColDamage = !C.MeterColDamage; break;
            case Col.Crit: C.MeterColCrit = !C.MeterColCrit; break;
            case Col.Dh: C.MeterColDh = !C.MeterColDh; break;
            case Col.Deaths: C.MeterColDeaths = !C.MeterColDeaths; break;
            case Col.Hps: C.MeterColHps = !C.MeterColHps; break;
            case Col.Taken: C.MeterColTaken = !C.MeterColTaken; break;
        }
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

    private static void Cell(string text, bool mine = false, bool dim = false)
    {
        ImGui.TableNextColumn();
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
