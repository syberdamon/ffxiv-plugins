using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;

namespace MitPlanner.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private int fightIndex = -1;
    private string? transferMessage;
    private Configuration Config => plugin.Configuration;

    public ConfigWindow(Plugin plugin) : base("Mit Planner - Options###MPConfig")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(640, 460), MaximumSize = new Vector2(1600, 1600) };
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##mptabs")) return;
        if (ImGui.BeginTabItem("Plan")) { DrawPlan(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Display")) { DrawDisplay(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Status")) { DrawStatus(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    // ================================================================ Plan

    private void DrawPlan()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) { ImGui.TextUnformatted("Log in to edit plans."); return; }

        // Fight picker: defaults to the fight you're in.
        if (fightIndex < 0)
        {
            var here = plugin.Runner.Fight;
            fightIndex = here != null ? Math.Max(0, Fights.All.ToList().IndexOf(here)) : 0;
        }
        var names = Fights.All.Select(f => f.Name).ToArray();
        ImGui.SetNextItemWidth(260);
        ImGui.Combo("Fight", ref fightIndex, names, names.Length);
        var fight = Fights.All[fightIndex];
        var timeline = Fights.Load(fight);

        var jobId = player.ClassJob.RowId;
        var jobName = player.ClassJob.ValueNullable?.Name.ExtractText() ?? "this job";
        ImGui.SameLine();
        ImGui.TextDisabled($"Plan for {jobName} (switch job to edit another job's plan)");

        var plan = Config.PlanFor(fight.TerritoryId, jobId);
        var catalog = ActionCatalog.ForCurrentJob();

        // QC: every planned use must have its cooldown back by then.
        var conflicts = PlanCheck.Run(timeline, plan, catalog);
        if (plan.Count == 0) ImGui.TextDisabled("QC: no cooldowns planned yet.");
        else if (conflicts.Count == 0) ImGui.TextColored(Style.Ok, $"QC: all {plan.Count} planned cooldowns will be ready.");
        else ImGui.TextColored(Style.Warning, $"QC: {conflicts.Count} planned cooldown(s) won't be back in time (marked below).");
        ImGui.SameLine();
        ImGui.TextDisabled("  Times are fight time; '~' = estimated across a phase change.");

        if (ImGui.SmallButton("Copy plan to clipboard"))
        {
            ImGui.SetClipboardText(PlanTransfer.Export(fight, jobId, plan));
            transferMessage = $"Copied {plan.Count} entries.";
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Paste plan (replaces this job's plan for this fight)"))
        {
            var imported = PlanTransfer.Import(ImGui.GetClipboardText(), fight, jobId, out var error);
            if (imported != null)
            {
                Config.Plans[Configuration.PlanKey(fight.TerritoryId, jobId)] = imported;
                Config.Save();
                plan = imported;
                transferMessage = $"Pasted {imported.Count} entries.";
            }
            else transferMessage = error;
        }
        if (transferMessage != null) { ImGui.SameLine(); ImGui.TextDisabled(transferMessage); }

        if (!ImGui.BeginTable("##plan", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH,
                new Vector2(0, ImGui.GetContentRegionAvail().Y))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Fight time", ImGuiTableColumnFlags.WidthFixed, 96);
        ImGui.TableSetupColumn("Mechanic", ImGuiTableColumnFlags.WidthFixed, 200);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("Cooldowns (press N seconds early; Auto = plugin presses it)", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        var multi = timeline.Events.Where(e => !e.Hidden).GroupBy(e => e.Label).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

        foreach (var e in timeline.Events.Where(e => !e.Hidden))
        {
            ImGui.TableNextRow();
            ImGui.PushID(e.Index);
            var type = Config.TagFor(fight, e.Label);

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Style.FightTime(e));
            if (e.FightTimeEstimated && ImGui.IsItemHovered())
                ImGui.SetTooltip($"Estimated: cactbot lists this at {e.Time:0.0}s on its own phase clock.\nPhase changes depend on your kill speed.");

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Style.Color(type), multi.Contains(e.Label) ? $"{e.Label} #{e.Occurrence}" : e.Label);

            ImGui.TableNextColumn();
            var t = (int)type;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##type", ref t, Style.TypeNames, Style.TypeNames.Length))
            {
                Config.TagOverrides[Configuration.TagKey(fight.TerritoryId, e.Label)] = (MechanicType)t; // applies to every occurrence
                Config.Save();
            }

            ImGui.TableNextColumn();
            DrawEntries(plan, e, catalog, conflicts, timeline);

            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawEntries(System.Collections.Generic.List<PlanEntry> plan, TimelineEvent e, System.Collections.Generic.IReadOnlyList<CatalogEntry> catalog,
        System.Collections.Generic.Dictionary<Guid, PlanConflict> conflicts, Timeline timeline)
    {
        PlanEntry? remove = null;
        foreach (var p in plan.Where(p => p.EventKey == e.Key))
        {
            ImGui.PushID(p.Id.ToString());
            var ability = catalog.FirstOrDefault(c => c.ActionId == p.ActionId);
            if (ability != null)
            {
                var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(ability.Icon)).GetWrapOrEmpty();
                ImGui.Image(tex.Handle, new Vector2(20));
                ImGui.SameLine();
            }
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(ability?.Name ?? $"#{p.ActionId} (not on this job/level)");
            ImGui.SameLine();
            var lead = p.LeadSeconds;
            ImGui.SetNextItemWidth(60);
            if (ImGui.DragFloat("##lead", ref lead, 0.1f, 0f, 30f, "%.1fs")) { p.LeadSeconds = lead; Config.Save(); }
            ImGui.SameLine();
            var auto = p.AutoUse;
            if (ImGui.Checkbox("Auto", ref auto)) { p.AutoUse = auto; Config.Save(); }
            ImGui.SameLine();
            if (ImGui.SmallButton("x")) remove = p;

            if (conflicts.TryGetValue(p.Id, out var c))
            {
                ImGui.SameLine();
                ImGui.TextColored(Style.Warning, $"! not back until {Style.ClockWithSeconds(c.ReadyAt)}");
                if (ImGui.IsItemHovered())
                {
                    var by = c.BlockedBy != null ? $"{c.BlockedBy.Label} #{c.BlockedBy.Occurrence}" : "an earlier use";
                    var charges = c.MaxCharges > 1 ? $" ({c.MaxCharges} charges, {c.RecastSeconds:0}s each)" : $" ({c.RecastSeconds:0}s cooldown)";
                    ImGui.SetTooltip($"Planned at {Style.ClockWithSeconds(c.PlannedAt)}, but it's still recharging from {by}{charges}.\n" +
                                     $"Ready again at {Style.ClockWithSeconds(c.ReadyAt)}. Move it, lower the lead time, or pick another cooldown.");
                }
            }
            ImGui.PopID();
        }
        if (remove != null) { plan.Remove(remove); Config.Save(); }

        ImGui.SetNextItemWidth(180);
        if (ImGui.BeginCombo("##add", "+ Add cooldown"))
        {
            foreach (var c in catalog)
            {
                if (ImGui.Selectable($"{c.Name}  ({c.RecastSeconds:0}s)##{c.ActionId}"))
                {
                    plan.Add(new PlanEntry { EventKey = e.Key, ActionId = c.ActionId });
                    Config.Save();
                }
            }
            ImGui.EndCombo();
        }
    }

    // ================================================================ Display

    private void DrawDisplay()
    {
        var show = Config.ShowTimeline;
        if (ImGui.Checkbox("Show timeline window", ref show)) { Config.ShowTimeline = show; Config.Save(); }

        var count = Config.UpcomingCount;
        if (ImGui.SliderInt("Upcoming mechanics shown", ref count, 1, 15)) { Config.UpcomingCount = count; Config.Save(); }

        var tagged = Config.ShowOnlyTagged;
        if (ImGui.Checkbox("Only show tagged mechanics (hide 'Other')", ref tagged)) { Config.ShowOnlyTagged = tagged; Config.Save(); }

        var locked = Config.LockWindows;
        if (ImGui.Checkbox("Lock window positions", ref locked)) { Config.LockWindows = locked; Config.Save(); }

        ImGui.Separator();
        var scale = Config.CalloutScale;
        if (ImGui.SliderFloat("Callout text size", ref scale, 1f, 3f, "%.1fx")) { Config.CalloutScale = scale; Config.Save(); }

        var heads = Config.CalloutHeadsUp;
        if (ImGui.SliderFloat("Callout heads-up before press time", ref heads, 0f, 10f, "%.0fs")) { Config.CalloutHeadsUp = heads; Config.Save(); }

        var chat = Config.ChatCallouts;
        if (ImGui.Checkbox("Also print callouts to chat", ref chat)) { Config.ChatCallouts = chat; Config.Save(); }

        var place = plugin.ShowCalloutPlacement;
        if (ImGui.Checkbox("Show callout area (to move it)", ref place)) plugin.ShowCalloutPlacement = place;

        ImGui.Separator();
        var allowAuto = Config.AllowAutoUse;
        if (ImGui.Checkbox("Allow auto-use (only for plan entries ticked 'Auto')", ref allowAuto)) { Config.AllowAutoUse = allowAuto; Config.Save(); }
        ImGui.TextDisabled("Auto-use presses combat actions for you. That's automation: same risk category as Tank Stance Manager.");

        ImGui.Separator();
        var fight = Fights.All[Math.Max(0, fightIndex)];
        if (!plugin.Runner.Preview)
        {
            if (ImGui.Button($"Preview {fight.Name} timeline")) plugin.Runner.StartPreview(fight);
            ImGui.SameLine();
            ImGui.TextDisabled("Runs the clock from 0 anywhere, no syncing, no auto-use.");
        }
        else if (ImGui.Button("Stop preview")) plugin.Runner.Stop();
    }

    // ================================================================ Status

    private void DrawStatus()
    {
        var r = plugin.Runner;
        ImGui.TextUnformatted($"Zone ID: {Plugin.ClientState.TerritoryType}");
        ImGui.TextUnformatted($"Fight: {r.Fight?.Name ?? "none (no timeline for this zone)"}");
        ImGui.TextUnformatted($"Clock: {(r.Running ? Style.Clock(r.Now) : "stopped")}{(r.Preview ? " (preview)" : "")}");
        ImGui.TextUnformatted($"Last sync: {r.LastSync}");
        ImGui.TextUnformatted($"Active callouts: {plugin.Tracker.Active.Count}");
        ImGui.Spacing();
        ImGui.TextDisabled("Bundled fights: " + string.Join(", ", Fights.All.Select(f => $"{f.Name} ({f.TerritoryId})")));
    }
}
