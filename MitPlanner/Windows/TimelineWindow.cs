using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;

namespace MitPlanner.Windows;

/// <summary>Next N mechanics with countdowns, type badges, and your planned cooldowns.</summary>
public sealed class TimelineWindow : Window
{
    private readonly Plugin plugin;

    public TimelineWindow(Plugin plugin) : base("Mit Planner###MPTimeline")
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        IsOpen = true;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(260, 80), MaximumSize = new Vector2(900, 900) };
    }

    public override bool DrawConditions() => plugin.Configuration.ShowTimeline && plugin.Runner.Timeline != null;

    public override void PreDraw()
    {
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoDocking |
                ImGuiWindowFlags.NoCollapse;
        if (plugin.Configuration.LockWindows) Flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        ShowCloseButton = false;
        var runner = plugin.Runner;
        WindowName = $"{runner.Fight?.Name ?? "Mit Planner"}{(runner.Preview ? " (preview)" : "")}  {(runner.Running ? Style.Clock(runner.Now) : "")}###MPTimeline";
    }

    public override void Draw()
    {
        var config = plugin.Configuration;
        var runner = plugin.Runner;
        var fight = runner.Fight!;
        var plan = plugin.Tracker.CurrentPlan();
        var conflicts = plan != null && runner.Timeline != null
            ? PlanCheck.Run(runner.Timeline, plan, ActionCatalog.ForCurrentJob())
            : new System.Collections.Generic.Dictionary<System.Guid, PlanConflict>();
        var now = runner.Running ? runner.Now : 0;

        // Quick toggles: MT/OT role, run the plan at all, auto-use, callouts.
        if (ToggleButton(config.Role == TankRole.OffTank ? "Role: OT" : "Role: MT", true))
        {
            config.Role = config.Role == TankRole.OffTank ? TankRole.MainTank : TankRole.OffTank;
            config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click to switch between your MT and OT plans.");
        ImGui.SameLine();
        if (ToggleButton(config.PlanEnabled ? "Plan: ON" : "Plan: OFF", config.PlanEnabled))
        {
            config.PlanEnabled = !config.PlanEnabled;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(!config.PlanEnabled);
        if (ToggleButton(config.AllowAutoUse ? "Auto: ON" : "Auto: OFF", config.AllowAutoUse && config.PlanEnabled))
        {
            config.AllowAutoUse = !config.AllowAutoUse;
            config.Save();
        }
        ImGui.SameLine();
        if (ToggleButton(config.ShowCallouts ? "Callouts: ON" : "Callouts: OFF", config.ShowCallouts && config.PlanEnabled))
        {
            config.ShowCallouts = !config.ShowCallouts;
            config.Save();
        }
        ImGui.EndDisabled();
        if (!runner.Running) { ImGui.SameLine(); ImGui.TextDisabled("Waiting for the pull..."); }

        var events = runner.Upcoming(config.UpcomingCount,
            config.ShowOnlyTagged ? (System.Func<TimelineEvent, bool>)(e => config.TagFor(fight, e.Label) != MechanicType.Other) : null);

        // Drag a header to reorder, drag a column edge to resize, right-click a header to hide/show.
        // ImGui remembers the layout between sessions.
        if (!ImGui.BeginTable("##upcoming", 4,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable))
            return;
        ImGui.TableSetupColumn("In", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Mechanic", ImGuiTableColumnFlags.WidthFixed, 200);
        ImGui.TableSetupColumn("Cooldowns", ImGuiTableColumnFlags.WidthFixed, 80);
        if (config.ShowTimelineHeaders) ImGui.TableHeadersRow();

        foreach (var e in events)
        {
            var type = config.TagFor(fight, e.Label);
            var color = Style.Color(type);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Style.ClockWithSeconds(e.Time - now));

            ImGui.TableNextColumn();
            ImGui.TextColored(color, Style.Badge(type));

            ImGui.TableNextColumn();
            ImGui.TextColored(color, e.Label);
            // Tank busters coming up soon: who's it on?
            if (type == MechanicType.TankBuster && runner.Running && e.Time - now <= 12)
            {
                var t = plugin.Targeting.Check(fight, e);
                if (t.Source != TargetSource.Unknown)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(t.IsMe ? Style.Warning : Style.Ok, t.IsMe ? $"(you, {t.Describe()})" : $"(not you, {t.Describe()})");
                }
            }

            ImGui.TableNextColumn();
            if (plan == null) continue;
            var first = true;
            foreach (var p in plan.Where(p => p.EventKey == e.Key))
            {
                if (ActionCatalog.Find(p.ActionId) is not { } ability) continue;
                if (!first) ImGui.SameLine(0, 2);
                first = false;
                var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(ability.Icon)).GetWrapOrEmpty();
                ImGui.Image(tex.Handle, new Vector2(ImGui.GetTextLineHeight() + 2));
                var conflict = conflicts.ContainsKey(p.Id);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{ability.Name} ({p.LeadSeconds:0.#}s early{(p.AutoUse && config.AllowAutoUse ? ", auto" : "")})" +
                                     (conflict ? "\nPlan conflict: won't be off cooldown in time" : ""));
                if (conflict) { ImGui.SameLine(0, 1); ImGui.TextColored(Style.Warning, "!"); }
            }
        }
        ImGui.EndTable();
    }

    private static readonly Vector4 OnColor = new(0.20f, 0.50f, 0.25f, 1f);
    private static readonly Vector4 OffColor = new(0.35f, 0.35f, 0.38f, 1f);

    private static bool ToggleButton(string label, bool on)
    {
        var color = on ? OnColor : OffColor;
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color * 1.2f);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color * 0.9f);
        var pressed = ImGui.SmallButton(label);
        ImGui.PopStyleColor(3);
        return pressed;
    }
}
