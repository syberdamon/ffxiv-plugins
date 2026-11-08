using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MaterialStash.Core;

namespace MaterialStash.Windows;

/// <summary>
/// A small bar sitting on top of the retainer list (right-aligned, so it doesn't cover
/// AutoRetainer's own bar on the left): Stash, Scan, Stop, and the status line.
/// </summary>
public sealed unsafe class BellOverlay : Window
{
    private readonly Plugin plugin;
    private Vector2 size = new(320, 60);
    private DateTime nextCount = DateTime.MinValue;
    private int stacks, crystalTypes, homeless, tidyMoves;
    private string tidyTip = "";

    public BellOverlay(Plugin plugin) : base("Material Stash###MaterialStashBell")
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    public override bool DrawConditions() =>
        (plugin.Configuration.ShowBellButtons || plugin.Runner.Busy) && GameUi.IsOpen("RetainerList");

    public override void PreDraw()
    {
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove;
        ShowCloseButton = false;
        var list = GameUi.Get("RetainerList");
        if (list == null || list->RootNode == null) return;
        var width = list->RootNode->Width * list->Scale;
        var c = plugin.Configuration;
        var pos = new Vector2(list->X + width - size.X + c.BellOffsetX, list->Y - size.Y - 2 + c.BellOffsetY);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().Pos + pos, ImGuiCond.Always);
    }

    public override void Draw()
    {
        var r = plugin.Runner;
        if (DateTime.UtcNow >= nextCount)
        {
            nextCount = DateTime.UtcNow.AddSeconds(1);
            var mats = plugin.Planner.PreviewMaterials();
            stacks = mats.Count(m => m.To != null);
            homeless = mats.Count(m => m.To == null);
            crystalTypes = plugin.Planner.PreviewCrystals().Count(x => x.Retainer != null);
            var plan = plugin.Planner.TidyPlan();
            tidyMoves = plan.Sum(m => m.Stacks);
            var merges = plan.Where(m => m.Merge).Sum(m => m.Stacks);
            tidyTip = tidyMoves == 0 ? "Everything is where it belongs (as last seen)."
                : $"Move {tidyMoves} stack(s): {merges} merged with the rest of their item, {tidyMoves - merges} to their homes.\n" +
                  string.Join("\n", plan.Take(12).Select(m => $"  {Items.Info(m.ItemId)?.Name}: {plugin.Tracker.NameOf(m.From)} -> {plugin.Tracker.NameOf(m.To)}")) +
                  (plan.Count > 12 ? $"\n  ... {plan.Count - 12} more" : "") + "\nGoes through your bags (uses your free slots). /stash tidy";
        }

        if (r.Busy)
        {
            if (ImGui.Button("Stop##stash")) r.Stop();
        }
        else
        {
            var why = r.CantStart();
            ImGui.BeginDisabled(why != null || stacks + crystalTypes == 0);
            if (ImGui.Button($"Stash mats ({stacks} stack{(stacks == 1 ? "" : "s")}{(crystalTypes > 0 ? $", {crystalTypes} crystal type{(crystalTypes == 1 ? "" : "s")}" : "")})##stash"))
                r.Start(scan: false);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(why ?? (stacks + crystalTypes == 0 ? "Nothing in your bags to stash." : "Visit each retainer that something goes to and deposit it. /stash deposit"));
            ImGui.SameLine();
            ImGui.BeginDisabled(why != null);
            if (ImGui.Button("Scan all##stash")) r.Start(scan: true);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(why ?? "Open every retainer once and record what it holds (nothing moves). /stash scan");
            ImGui.SameLine();
            ImGui.BeginDisabled(why != null || tidyMoves == 0);
            if (ImGui.Button($"Tidy ({tidyMoves})##tidy")) r.Start(Runner.RunMode.Tidy);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(why ?? tidyTip);
            if (plugin.Configuration.AutoSell)
            {
                var toSell = plugin.Selling.InBags().Sum(x => x.Value);
                ImGui.SameLine();
                ImGui.BeginDisabled(why != null || toSell == 0);
                if (ImGui.Button($"Sell ({toSell})##sell")) r.Start(Runner.RunMode.Sell);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(why ?? "List the to-sell items at your selling retainer. /stash sell");
            }
            var me = plugin.Tracker.Me();
            var furn = me == null ? 0 : me.Retainers.Where(kv => !me.Excluded.Contains(kv.Key)).Select(kv => kv.Value).Sum(s => s.Items.Where(kv => plugin.Storeroom.IsIndoorFurnishing(kv.Key)).Sum(kv => s.Stacks.GetValueOrDefault(kv.Key, 1)));
            if (furn > 0)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(why != null);
                if (ImGui.Button($"Furnishings home ({furn})##furn")) r.Start(Runner.RunMode.Furnish);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(why ?? "Takes indoor furnishings off your retainers (as many as your bags hold), then goes home (Lifestream)\n" +
                                            "and stores them in the house storeroom. Renovation and outdoor items stay where they are. /stash furnish");
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("...##stashcfg")) plugin.ToggleMain();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Material Stash settings (/stash)");
        }
        var status = r.Status;
        if (!r.Busy && homeless > 0) status = $"{homeless} stack(s) have no retainer with room: set an overflow order." + (status.Length > 0 ? "\n" + status : "");
        if (status.Length > 0) ImGui.TextUnformatted(status);
        size = ImGui.GetWindowSize();
    }
}
