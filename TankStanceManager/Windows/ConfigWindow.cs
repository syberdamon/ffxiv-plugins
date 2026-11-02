using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace TankStanceManager.Windows;

/// <summary>Full settings and live status. ON/OFF and MT/OT live on the control panel.</summary>
public sealed class ConfigWindow : Window
{
    private readonly Configuration config;
    private readonly StanceController controller;

    public ConfigWindow(Plugin plugin) : base("Tank Stance Manager - Options###TSMConfig")
    {
        config = plugin.Configuration;
        controller = plugin.Controller;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(380, 380), MaximumSize = new Vector2(800, 900) };
    }

    public override void Draw()
    {
        ImGui.TextDisabled($"Currently {(config.Enabled ? "ON" : "OFF")} as {(config.Role == TankRole.MainTank ? "MT" : "OT")} (change on the control panel or /tsm on|off|mt|ot)");

        if (ImGui.CollapsingHeader("Off-tank settings", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextUnformatted("Enmity band (% of the main tank's enmity)");

            var low = config.LowThreshold;
            if (ImGui.SliderInt("Stance ON below", ref low, 1, 99, "%d%%"))
            {
                config.LowThreshold = low;
                if (config.HighThreshold <= low) config.HighThreshold = low + 1;
                config.Save();
            }

            var high = config.HighThreshold;
            if (ImGui.SliderInt("Stance OFF at", ref high, 2, 100, "%d%%"))
            {
                config.HighThreshold = high;
                if (config.LowThreshold >= high) config.LowThreshold = high - 1;
                config.Save();
            }

            var rank3 = config.TreatRankThreeAsLow;
            if (ImGui.Checkbox("Also turn stance on if pushed to #3", ref rank3)) { config.TreatRankThreeAsLow = rank3; config.Save(); }

            var duties = config.DutiesOnly;
            if (ImGui.Checkbox("Only in duties", ref duties)) { config.DutiesOnly = duties; config.Save(); }

            var coTank = config.RequireCoTank;
            if (ImGui.Checkbox("Only when another tank is in the party", ref coTank)) { config.RequireCoTank = coTank; config.Save(); }
        }

        if (ImGui.CollapsingHeader("General", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var interval = config.MinToggleIntervalMs;
            if (ImGui.SliderInt("Min ms between presses", ref interval, 1000, 10000)) { config.MinToggleIntervalMs = interval; config.Save(); }

            var chat = config.ChatFeedback;
            if (ImGui.Checkbox("Print toggles to chat", ref chat)) { config.ChatFeedback = chat; config.Save(); }
        }

        if (ImGui.CollapsingHeader("Control panel", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var lockPanel = config.LockControlWindow;
            if (ImGui.Checkbox("Lock panel position", ref lockPanel)) { config.LockControlWindow = lockPanel; config.Save(); }

            var status = config.ShowStatusLine;
            if (ImGui.Checkbox("Show status line on panel", ref status)) { config.ShowStatusLine = status; config.Save(); }
        }

        if (ImGui.CollapsingHeader("Live status", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextUnformatted($"State: {controller.StatusText}");
            ImGui.TextUnformatted($"Stance detected: {(controller.StanceOn switch { true => "ON", false => "OFF", null => "n/a" })}");
            if (controller.MyRank > 0)
                ImGui.TextUnformatted($"Your enmity: #{controller.MyRank} at {controller.MyPercent}%");
            ImGui.TextWrapped($"Last action: {controller.LastAction}");
        }
    }
}
