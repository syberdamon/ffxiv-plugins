using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MapClickNav.Windows;

/// <summary>Small window that appears only while a trip is mounting or traveling.</summary>
public sealed class StopWindow : Window
{
    private static readonly Vector4 StopColor = new(0.65f, 0.20f, 0.20f, 1f);
    private readonly Navigator navigator;

    public StopWindow(Navigator navigator) : base("Map Click Nav###MCNStop")
    {
        this.navigator = navigator;
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoFocusOnAppearing;
        RespectCloseHotkey = false;
        ShowCloseButton = false;
        IsOpen = true; // visibility is decided by DrawConditions
    }

    public override bool DrawConditions() => navigator.State != TripState.Idle;

    public override void Draw()
    {
        ImGui.TextUnformatted(navigator.StatusText);

        ImGui.PushStyleColor(ImGuiCol.Button, StopColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, StopColor * 1.2f);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, StopColor * 0.9f);
        if (ImGui.Button("Stop", new Vector2(120, 0))) navigator.StopTrip();
        ImGui.PopStyleColor(3);
    }
}
