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

    public override bool DrawConditions() => plugin.GcRunner.Busy || plugin.ScripRunner.Busy;

    public override void Draw()
    {
        if (plugin.ScripRunner.Busy)
        {
            var s = plugin.ScripRunner;
            ImGui.TextUnformatted(s.Status);
            ImGui.TextDisabled($"Turned in {s.TurnedIn} (+{s.ScripsEarned} scrips), bought {s.Bought}");
            if (ImGui.Button("Stop##scrips")) s.Stop();
            return;
        }
        var r = plugin.GcRunner;
        ImGui.TextUnformatted(r.Status);
        ImGui.TextDisabled($"Delivered {r.Delivered} (+{r.SealsEarned} seals)" + (r.Bought.Count > 0 ? $", bought {string.Join(", ", r.Bought)}" : ""));
        if (ImGui.Button("Stop")) r.Stop();
    }
}
