using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MaterialStash.Core;

namespace MaterialStash.Windows;

/// <summary>Two buttons on top of the open saddlebag: mats into it, mats out of it.</summary>
public sealed unsafe class SaddleOverlay : Window
{
    private readonly Plugin plugin;
    private Vector2 size = new(300, 30);
    private DateTime nextCount = DateTime.MinValue;
    private int toSaddle, toBags;

    public SaddleOverlay(Plugin plugin) : base("Material Stash Saddlebag###MaterialStashSaddle")
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    private static string Addon => GameUi.IsOpen("InventoryBuddy") ? "InventoryBuddy" : "InventoryBuddy2";

    public override bool DrawConditions() =>
        (plugin.Configuration.ShowSaddlebagButtons || plugin.Saddle.Busy) && Tracker.SaddlebagOpen;

    public override void PreDraw()
    {
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove;
        ShowCloseButton = false;
        var a = GameUi.Get(Addon);
        if (a == null || a->RootNode == null) return;
        var width = a->RootNode->Width * a->Scale;
        var pos = new Vector2(a->X + width - size.X, a->Y - size.Y - 2);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().Pos + pos, ImGuiCond.Always);
    }

    public override void Draw()
    {
        var s = plugin.Saddle;
        if (DateTime.UtcNow >= nextCount)
        {
            nextCount = DateTime.UtcNow.AddSeconds(1);
            toSaddle = s.Pending(SaddleMover.Direction.ToSaddlebag);
            toBags = s.Pending(SaddleMover.Direction.ToBags);
        }
        if (s.Busy)
        {
            if (ImGui.Button("Stop##saddle")) s.Stop();
        }
        else
        {
            ImGui.BeginDisabled(toSaddle == 0);
            if (ImGui.Button($"Mats -> saddlebag ({toSaddle})##in")) s.Start(SaddleMover.Direction.ToSaddlebag);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(toBags == 0);
            if (ImGui.Button($"Mats -> bags ({toBags})##out")) s.Start(SaddleMover.Direction.ToBags);
            ImGui.EndDisabled();
        }
        if (s.Status.Length > 0) ImGui.TextUnformatted(s.Status);
        size = ImGui.GetWindowSize();
    }
}
