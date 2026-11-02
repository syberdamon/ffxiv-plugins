using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace TankStanceManager.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Configuration config;
    private readonly StanceController controller;

    public ConfigWindow(Plugin plugin) : base("Tank Stance Manager###TSMConfig")
    {
        config = plugin.Configuration;
        controller = plugin.Controller;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(380, 360), MaximumSize = new Vector2(800, 900) };
    }

    public override void Draw()
    {
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled)) { config.Enabled = enabled; config.Save(); }

        ImGui.Separator();
        ImGui.TextUnformatted("Role");
        var role = (int)config.Role;
        var changed = ImGui.RadioButton("Main tank (stance on at all times)", ref role, 0);
        changed |= ImGui.RadioButton("Off tank (hold enmity band)", ref role, 1);
        if (changed) { config.Role = (TankRole)role; config.Save(); }

        if (config.Role == TankRole.OffTank)
        {
            ImGui.Spacing();
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

            var duties = config.DutiesOnly;
            if (ImGui.Checkbox("Only in duties", ref duties)) { config.DutiesOnly = duties; config.Save(); }

            var coTank = config.RequireCoTank;
            if (ImGui.Checkbox("Only when another tank is in the party", ref coTank)) { config.RequireCoTank = coTank; config.Save(); }

            var rank3 = config.TreatRankThreeAsLow;
            if (ImGui.Checkbox("Also turn stance on if pushed to #3", ref rank3)) { config.TreatRankThreeAsLow = rank3; config.Save(); }
        }

        ImGui.Separator();
        var interval = config.MinToggleIntervalMs;
        if (ImGui.SliderInt("Min ms between presses", ref interval, 1000, 10000)) { config.MinToggleIntervalMs = interval; config.Save(); }

        var chat = config.ChatFeedback;
        if (ImGui.Checkbox("Print toggles to chat", ref chat)) { config.ChatFeedback = chat; config.Save(); }

        ImGui.Separator();
        ImGui.TextUnformatted("Live status");
        ImGui.TextUnformatted($"State: {controller.StatusText}");
        ImGui.TextUnformatted($"Stance detected: {(controller.StanceOn switch { true => "ON", false => "OFF", null => "n/a" })}");
        if (controller.MyRank > 0)
            ImGui.TextUnformatted($"Your enmity: #{controller.MyRank} at {controller.MyPercent}%");
        ImGui.TextWrapped($"Last action: {controller.LastAction}");
    }
}
