using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace TankStanceManager.Windows;

/// <summary>
/// Small always-available panel: ON/OFF, MT/OT, and a button to open Options.
/// </summary>
public sealed class ControlWindow : Window
{
    private static readonly Vector4 OnColor = new(0.20f, 0.55f, 0.25f, 1f);
    private static readonly Vector4 OffColor = new(0.60f, 0.20f, 0.20f, 1f);
    private static readonly Vector4 SelectedColor = new(0.25f, 0.40f, 0.70f, 1f);
    private static readonly Vector4 IdleColor = new(0.25f, 0.25f, 0.28f, 1f);

    private readonly Plugin plugin;
    private readonly Configuration config;
    private readonly StanceController controller;

    public ControlWindow(Plugin plugin) : base("TSM###TSMControl")
    {
        this.plugin = plugin;
        config = plugin.Configuration;
        controller = plugin.Controller;
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking;
        RespectCloseHotkey = false; // Escape shouldn't hide the panel.
    }

    public override void PreDraw()
    {
        if (config.LockControlWindow) Flags |= ImGuiWindowFlags.NoMove;
        else Flags &= ~ImGuiWindowFlags.NoMove;
    }

    public override void OnOpen() { if (!config.ShowControlWindow) { config.ShowControlWindow = true; config.Save(); } }
    public override void OnClose() { if (config.ShowControlWindow) { config.ShowControlWindow = false; config.Save(); } }

    public override void Draw()
    {
        // ON / OFF
        if (ColoredButton(config.Enabled ? "ON" : "OFF", config.Enabled ? OnColor : OffColor, new Vector2(50, 0)))
        {
            config.Enabled = !config.Enabled;
            config.Save();
        }

        // MT / OT
        ImGui.SameLine();
        if (ColoredButton("MT", config.Role == TankRole.MainTank ? SelectedColor : IdleColor, new Vector2(36, 0)))
        {
            config.Role = TankRole.MainTank;
            config.Save();
        }
        ImGui.SameLine(0, 2);
        if (ColoredButton("OT", config.Role == TankRole.OffTank ? SelectedColor : IdleColor, new Vector2(36, 0)))
        {
            config.Role = TankRole.OffTank;
            config.Save();
        }

        // Options
        ImGui.SameLine();
        if (ImGui.Button("Options")) plugin.ToggleOptions();

        if (config.ShowStatusLine)
            ImGui.TextDisabled(StatusLine());
    }

    private string StatusLine()
    {
        if (!config.Enabled) return "Disabled";
        if (config.Role == TankRole.OffTank && controller.MyRank > 0)
            return $"#{controller.MyRank} at {controller.MyPercent}%  |  stance {(controller.StanceOn == true ? "on" : "off")}";
        return controller.StatusText;
    }

    private static bool ColoredButton(string label, Vector4 color, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color * 1.2f);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color * 0.9f);
        var pressed = ImGui.Button(label, size);
        ImGui.PopStyleColor(3);
        return pressed;
    }
}
