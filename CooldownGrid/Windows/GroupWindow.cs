using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;

namespace CooldownGrid.Windows;

/// <summary>One group's grid of clickable cooldown icons.</summary>
public sealed class GroupWindow : Window
{
    private const uint DimColor = 0xB0000000;       // ABGR: translucent black
    private const uint UnusableColor = 0x500000FF;  // ABGR: faint red
    private const uint TextColor = 0xFFFFFFFF;
    private const uint ShadowColor = 0xFF000000;
    private const uint ChargeColor = 0xFF7FFFFF;    // ABGR: light yellow

    private readonly Plugin plugin;
    public CooldownGroup Group { get; }

    public GroupWindow(Plugin plugin, CooldownGroup group) : base($"{group.Name}###CDG{group.Id}")
    {
        this.plugin = plugin;
        Group = group;
        RespectCloseHotkey = false;
        IsOpen = true; // shown/hidden by DrawConditions
    }

    public override bool DrawConditions() =>
        Group.Visible && Plugin.ObjectTable.LocalPlayer != null && Items().Any();

    public override void PreDraw()
    {
        WindowName = $"{Group.Name}###CDG{Group.Id}";
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoCollapse;
        if (Group.Locked) Flags |= ImGuiWindowFlags.NoMove;
        if (!Group.ShowTitleBar) Flags |= ImGuiWindowFlags.NoTitleBar;
        if (!Group.ShowBackground) Flags |= ImGuiWindowFlags.NoBackground;
        ShowCloseButton = false;
    }

    public override void Draw()
    {
        var size = new Vector2(Group.IconSize);
        var columns = Math.Max(1, Group.Columns);
        var col = 0;

        foreach (var entry in Items())
        {
            if (col > 0) ImGui.SameLine();
            DrawIcon(entry, size);
            col = (col + 1) % columns;
        }
    }

    private System.Collections.Generic.IEnumerable<CatalogEntry> Items()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) yield break;

        foreach (var a in plugin.Configuration.ForJob(player.ClassJob.RowId))
        {
            if (a.GroupId != Group.Id) continue;
            if (ActionCatalog.Find(a.ActionId) is { } entry) yield return entry; // skips abilities above your level
        }
    }

    private void DrawIcon(CatalogEntry entry, Vector2 size)
    {
        var config = plugin.Configuration;
        // Only abilities the game allows on a party member are cast on the co-tank.
        var sendable = Group.CastOnCoTank && entry.CanTargetAlly;
        var coTank = sendable ? ActionState.CoTank() : null;
        var status = coTank is { } ct ? ActionState.Get(entry.ActionId, ct.Id) : ActionState.Get(entry.ActionId);
        var texture = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(entry.Icon)).GetWrapOrEmpty();

        var topLeft = ImGui.GetCursorScreenPos();
        ImGui.PushID((int)entry.ActionId);
        ImGui.Image(texture.Handle, size);
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var hovered = ImGui.IsItemHovered();
        ImGui.PopID();

        var draw = ImGui.GetWindowDrawList();
        var bottomRight = topLeft + size;

        if (status.OnCooldown)
            draw.AddRectFilled(topLeft, bottomRight, DimColor);
        else if (config.TintUnusable && !status.Usable)
            draw.AddRectFilled(topLeft, bottomRight, UnusableColor);

        // Seconds remaining, centered.
        if (config.ShowCooldownNumbers && status.Remaining > 0.05f)
        {
            var text = status.Remaining >= 60 ? $"{(int)status.Remaining / 60}:{(int)status.Remaining % 60:00}"
                     : status.Remaining >= 10 ? $"{(int)status.Remaining}"
                     : $"{status.Remaining:0.0}";
            var textSize = ImGui.CalcTextSize(text);
            var pos = topLeft + (size - textSize) / 2;
            draw.AddText(pos + new Vector2(1, 1), ShadowColor, text);
            draw.AddText(pos, TextColor, text);
        }

        // Charges, bottom-right.
        if (status.MaxCharges > 1)
        {
            var text = status.Charges.ToString();
            var textSize = ImGui.CalcTextSize(text);
            var pos = bottomRight - textSize - new Vector2(2, 1);
            draw.AddText(pos + new Vector2(1, 1), ShadowColor, text);
            draw.AddText(pos, ChargeColor, text);
        }

        if (hovered && config.ShowTooltips)
        {
            var state = status.OnCooldown ? $"{status.Remaining:0.0}s" : status.Usable ? "Ready" : "Can't use right now";
            var to = sendable ? (coTank is { } c ? $"\n-> {c.Name}" : "\nNo other tank in the party") : "";
            var units = ActionCatalog.IsGeneralAction(entry.ActionId) ? "bars" : "charges";
            ImGui.SetTooltip(status.MaxCharges > 1
                ? $"{entry.Name}\n{status.Charges}/{status.MaxCharges} {units} - {state}{to}"
                : $"{entry.Name}\n{state}{to}");
        }

        if (clicked)
        {
            if (!sendable) ActionState.Use(entry.ActionId);
            else if (coTank is { } target) ActionState.Use(entry.ActionId, target.Id);
            else Plugin.ChatGui.Print($"[CooldownGrid] {entry.Name}: no other tank in the party to send it to.");
        }
    }
}
