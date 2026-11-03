using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PoeticsSpender.Windows;

/// <summary>Shown while a run is going: what it's doing, a Stop button, and first-time purchase confirmations.</summary>
public sealed class StatusWindow : Window
{
    private readonly Plugin plugin;

    public StatusWindow(Plugin plugin) : base("Poetics Spender###PoeticsStatus")
    {
        this.plugin = plugin;
        IsOpen = true;
        RespectCloseHotkey = false;
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking;
        ShowCloseButton = false;
    }

    public override bool DrawConditions() => plugin.Runner.Busy;

    public override void Draw()
    {
        var r = plugin.Runner;
        ImGui.TextUnformatted(r.Status);

        if (r.ConfirmText is { } text)
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), text);
            if (ImGui.Button("Buy")) r.Confirm();
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) r.Stop("Cancelled.");
            ImGui.TextDisabled("Confirmed rows are remembered; you won't be asked again for this one.");
            return;
        }
        if (ImGui.Button("Stop")) r.Stop();
    }
}
