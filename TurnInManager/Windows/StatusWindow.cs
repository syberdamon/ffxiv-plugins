using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace TurnInManager.Windows;

/// <summary>Small window while a run is going: what it's doing, and Stop.</summary>
public sealed class StatusWindow : Window
{
    private readonly Plugin plugin;

    public StatusWindow(Plugin plugin) : base("Turn-In Manager###TurnInStatus")
    {
        this.plugin = plugin;
        IsOpen = true;
        RespectCloseHotkey = false;
        ShowCloseButton = false;
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking;
    }

    public override bool DrawConditions() => plugin.GcRunner.Busy;

    public override void Draw()
    {
        var r = plugin.GcRunner;
        ImGui.TextUnformatted(r.Status);
        ImGui.TextDisabled($"Delivered {r.Delivered} (+{r.SealsEarned} seals)" + (r.Bought.Count > 0 ? $", bought {string.Join(", ", r.Bought)}" : ""));
        if (ImGui.Button("Stop")) r.Stop();
    }
}
