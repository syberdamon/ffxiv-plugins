using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;

namespace CooldownGrid.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private Configuration Config => plugin.Configuration;

    public ConfigWindow(Plugin plugin) : base("Cooldown Grid - Options###CDGConfig")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(520, 420), MaximumSize = new Vector2(1200, 1400) };
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##cdgtabs")) return;
        if (ImGui.BeginTabItem("Abilities")) { DrawAbilities(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Groups")) { DrawGroups(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Display")) { DrawDisplay(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    // ================================================================ Abilities

    private void DrawAbilities()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) { ImGui.TextUnformatted("Log in to configure abilities."); return; }

        var jobId = player.ClassJob.RowId;
        var jobName = player.ClassJob.ValueNullable?.Name.ExtractText() ?? "this job";
        var catalog = ActionCatalog.ForCurrentJob();
        var assignments = Config.ForJob(jobId);

        ImGui.TextWrapped($"Abilities for {jobName} (level {player.Level}). Pick a group for each one you want on screen. Saved per job.");
        if (Presets.For(jobId).Count > 0 && ImGui.SmallButton("Reset this job to the starter layout"))
        {
            Config.Jobs[jobId] = Presets.For(jobId);
            Config.Save();
            return;
        }
        ImGui.Spacing();

        var groupNames = new[] { "Hidden" }.Concat(Config.Groups.Select(g => g.Name)).ToArray();

        if (!ImGui.BeginTable("##abilities", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH,
                new Vector2(0, ImGui.GetContentRegionAvail().Y))) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 28);
        ImGui.TableSetupColumn("Ability", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Lv", ImGuiTableColumnFlags.WidthFixed, 28);
        ImGui.TableSetupColumn("CD", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("Group", ImGuiTableColumnFlags.WidthFixed, 160);
        ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed, 52);
        ImGui.TableHeadersRow();

        // Placed abilities first, in display order; then the rest by level.
        var placed = assignments.Select(a => catalog.FirstOrDefault(e => e.ActionId == a.ActionId)).Where(e => e != null).Cast<CatalogEntry>();
        var rest = catalog.Where(e => assignments.All(a => a.ActionId != e.ActionId));

        foreach (var entry in placed.Concat(rest).ToList())
        {
            ImGui.TableNextRow();
            ImGui.PushID((int)entry.ActionId);

            ImGui.TableNextColumn();
            var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(entry.Icon)).GetWrapOrEmpty();
            ImGui.Image(tex.Handle, new Vector2(24));

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(entry.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Level.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.RecastSeconds >= 60 ? $"{entry.RecastSeconds / 60:0.#}m" : $"{entry.RecastSeconds:0}s");

            ImGui.TableNextColumn();
            var index = assignments.FindIndex(a => a.ActionId == entry.ActionId);
            var current = index < 0 ? 0 : Config.Groups.FindIndex(g => g.Id == assignments[index].GroupId) + 1;
            if (current < 0) current = 0; // group was deleted
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##group", ref current, groupNames, groupNames.Length))
            {
                if (current == 0) { if (index >= 0) assignments.RemoveAt(index); }
                else if (index >= 0) assignments[index].GroupId = Config.Groups[current - 1].Id;
                else assignments.Add(new Assignment { ActionId = entry.ActionId, GroupId = Config.Groups[current - 1].Id });
                Config.Save();
            }

            ImGui.TableNextColumn();
            if (index >= 0)
            {
                if (ImGui.ArrowButton("##up", ImGuiDir.Up)) MoveWithinGroup(assignments, index, -1);
                ImGui.SameLine(0, 2);
                if (ImGui.ArrowButton("##down", ImGuiDir.Down)) MoveWithinGroup(assignments, index, +1);
            }

            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    /// <summary>Swap with the nearest ability in the same group in the given direction.</summary>
    private void MoveWithinGroup(List<Assignment> list, int index, int direction)
    {
        var group = list[index].GroupId;
        for (var j = index + direction; j >= 0 && j < list.Count; j += direction)
        {
            if (list[j].GroupId != group) continue;
            (list[index], list[j]) = (list[j], list[index]);
            Config.Save();
            return;
        }
    }

    // ================================================================ Groups

    private void DrawGroups()
    {
        ImGui.TextWrapped("Each group is its own window. The three default groups can be renamed but not deleted.");
        if (ImGui.Button("Add group"))
        {
            Config.Groups.Add(new CooldownGroup { Name = $"Group {Config.Groups.Count + 1}" });
            Config.Save();
            plugin.SyncGroupWindows();
        }
        ImGui.Separator();

        CooldownGroup? toDelete = null;
        foreach (var g in Config.Groups)
        {
            ImGui.PushID(g.Id.ToString());
            if (ImGui.CollapsingHeader($"{g.Name}###hdr", ImGuiTreeNodeFlags.DefaultOpen))
            {
                var name = g.Name;
                if (ImGui.InputText("Name", ref name, 40) && !string.IsNullOrWhiteSpace(name)) { g.Name = name; Config.Save(); }

                var visible = g.Visible;
                if (ImGui.Checkbox("Show window", ref visible)) { g.Visible = visible; Config.Save(); }
                ImGui.SameLine();
                var locked = g.Locked;
                if (ImGui.Checkbox("Lock position", ref locked)) { g.Locked = locked; Config.Save(); }
                ImGui.SameLine();
                var title = g.ShowTitleBar;
                if (ImGui.Checkbox("Title bar", ref title)) { g.ShowTitleBar = title; Config.Save(); }
                ImGui.SameLine();
                var bg = g.ShowBackground;
                if (ImGui.Checkbox("Background", ref bg)) { g.ShowBackground = bg; Config.Save(); }

                var columns = g.Columns;
                if (ImGui.SliderInt("Columns", ref columns, 1, 12)) { g.Columns = columns; Config.Save(); }
                var iconSize = g.IconSize;
                if (ImGui.SliderInt("Icon size", ref iconSize, 20, 96)) { g.IconSize = iconSize; Config.Save(); }

                if (!g.IsDefault && ImGui.Button("Delete group")) toDelete = g;
            }
            ImGui.PopID();
        }

        if (toDelete != null)
        {
            Config.Groups.Remove(toDelete);
            foreach (var list in Config.Jobs.Values) list.RemoveAll(a => a.GroupId == toDelete.Id); // back to Hidden
            Config.Save();
            plugin.SyncGroupWindows();
        }
    }

    // ================================================================ Display

    private void DrawDisplay()
    {
        var numbers = Config.ShowCooldownNumbers;
        if (ImGui.Checkbox("Show seconds remaining on icons", ref numbers)) { Config.ShowCooldownNumbers = numbers; Config.Save(); }

        var tooltips = Config.ShowTooltips;
        if (ImGui.Checkbox("Show ability name on hover", ref tooltips)) { Config.ShowTooltips = tooltips; Config.Save(); }

        var tint = Config.TintUnusable;
        if (ImGui.Checkbox("Tint red when off cooldown but unusable (no target, out of range...)", ref tint)) { Config.TintUnusable = tint; Config.Save(); }

        ImGui.Spacing();
        ImGui.TextDisabled("Tip: turn off a group's title bar and lock it to make it look like a hotbar.");
    }
}
