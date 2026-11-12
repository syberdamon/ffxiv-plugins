using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MaterialStash.Windows;

/// <summary>Small window while scrip goals or a collectables turn-in run: what's happening, Stop, and Resume after market buying.</summary>
public sealed class ScripWindow : Window
{
    private readonly Plugin plugin;

    public ScripWindow(Plugin plugin) : base("Scrip goals###StashScrips")
    {
        this.plugin = plugin;
        IsOpen = true;
        RespectCloseHotkey = false;
        ShowCloseButton = false;
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking;
    }

    public override bool DrawConditions() => plugin.Scrips.Busy || plugin.TurnIns.Busy;

    public override void Draw()
    {
        if (!plugin.Scrips.Busy)
        {
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
            ImGui.TextUnformatted(plugin.TurnIns.Status);
            ImGui.PopTextWrapPos();
            if (ImGui.Button("Stop")) plugin.TurnIns.Stop();
            return;
        }
        var s = plugin.Scrips;
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
        ImGui.TextUnformatted(s.Status);
        ImGui.PopTextWrapPos();
        if (s.WaitingForMarket)
        {
            foreach (var m in s.MarketList) ImGui.BulletText($"{m.Qty} x {m.Name} (lowest ~{m.Price:N0} gil)");
            if (ImGui.Button("Resume")) s.Resume();
            ImGui.SameLine();
            ImGui.TextDisabled("(anything still missing is gathered)");
        }
        if (ImGui.Button("Stop")) s.Stop();
    }
}
