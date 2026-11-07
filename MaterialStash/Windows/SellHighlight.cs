using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MaterialStash.Core;

namespace MaterialStash.Windows;

/// <summary>
/// Boxes the to-sell items in your bags. With the expanded inventory (all four bags shown) each stack
/// gets its own box; with the smaller layouts, which page is showing can't be told reliably, so the
/// whole inventory window is boxed and a note lists the bag and slot of each stack.
/// </summary>
public sealed unsafe class SellHighlight : Window
{
    private static readonly Vector4 Color = new(1f, 0.78f, 0.2f, 1f);
    private readonly Plugin plugin;
    private DateTime nextScan = DateTime.MinValue;
    private List<Tracker.Stack> stacks = new();

    public SellHighlight(Plugin plugin) : base("##MaterialStashSellHighlight")
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        IsOpen = true;
        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;
        Position = new Vector2(0, 0);
        Size = new Vector2(1, 1);
    }

    public override bool DrawConditions()
    {
        if (!plugin.Configuration.SellHighlight || plugin.Tracker.Me() is not { SellQueue.Count: > 0 } me) return false;
        if (DateTime.UtcNow >= nextScan)
        {
            nextScan = DateTime.UtcNow.AddSeconds(0.5);
            stacks = Tracker.Stacks(Tracker.Bags, id => me.SellQueue.Contains(id % 1_000_000));
        }
        return stacks.Count > 0 && (GameUi.IsOpen("InventoryExpansion") || GameUi.IsOpen("InventoryLarge") || GameUi.IsOpen("Inventory"));
    }

    public override void Draw()
    {
        var dl = ImGui.GetForegroundDrawList();
        var col = ImGui.GetColorU32(Color);
        var vp = ImGui.GetMainViewport().Pos;

        if (GameUi.IsOpen("InventoryExpansion"))
        {
            var drawn = 0;
            for (var bag = 0; bag < 4; bag++)
            {
                var grid = GameUi.Get($"InventoryGrid{bag}E");
                if (grid == null) continue;
                var slots = SlotNodes(grid);
                if (slots.Count != 35) continue;
                foreach (var s in stacks.Where(x => x.Type == Tracker.Bags[bag]))
                {
                    var n = slots[s.Slot].Node;
                    var size = new Vector2(n->Width, n->Height) * grid->Scale;
                    var min = vp + new Vector2(n->ScreenX, n->ScreenY);
                    dl.AddRect(min - new Vector2(1, 1), min + size + new Vector2(1, 1), col, 3f, ImDrawFlags.None, 2.5f);
                    drawn++;
                }
            }
            if (drawn == stacks.Count) return;
        }

        // Other layouts: box the inventory window and say where the stacks are.
        foreach (var name in new[] { "InventoryExpansion", "InventoryLarge", "Inventory" })
        {
            var a = GameUi.Get(name);
            if (a == null || a->RootNode == null) continue;
            var min = vp + new Vector2(a->X, a->Y);
            var max = min + new Vector2(a->RootNode->Width, a->RootNode->Height) * a->Scale;
            dl.AddRect(min, max, col, 4f, ImDrawFlags.None, 3f);
            var text = "To sell: " + string.Join(", ", stacks.Take(6).Select(s =>
                $"{Items.Info(s.ItemId)?.Name} x{s.Qty} (bag {Array.IndexOf(Tracker.Bags, s.Type) + 1}, slot {s.Slot + 1})")) +
                (stacks.Count > 6 ? $" +{stacks.Count - 6} more" : "");
            dl.AddText(new Vector2(min.X, max.Y + 4), col, text);
            break;
        }
    }

    /// <summary>The 35 slot nodes of an inventory grid, in slot order (rows top to bottom, left to right).</summary>
    private static List<Pointer> SlotNodes(AtkUnitBase* grid)
    {
        var list = new List<Pointer>();
        for (var i = 0; i < grid->UldManager.NodeListCount; i++)
        {
            var node = grid->UldManager.NodeList[i];
            if (node == null || (int)node->Type < 1000 || !node->IsVisible()) continue;
            var comp = ((AtkComponentNode*)node)->Component;
            if (comp == null || comp->GetComponentType() != ComponentType.DragDrop) continue;
            list.Add(new Pointer(node));
        }
        list.Sort((a, b) => a.Node->ScreenY != b.Node->ScreenY ? a.Node->ScreenY.CompareTo(b.Node->ScreenY) : a.Node->ScreenX.CompareTo(b.Node->ScreenX));
        return list;
    }

    private readonly struct Pointer
    {
        public readonly AtkResNode* Node;
        public Pointer(AtkResNode* n) => Node = n;
        public static implicit operator AtkResNode*(Pointer p) => p.Node;
    }
}
