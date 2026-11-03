using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PoeticsSpender.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin) : base("Poetics Spender Settings###PoeticsConfig")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 300), MaximumSize = new Vector2(900, 1200) };
    }

    private Configuration Config => plugin.Configuration;

    public override void OnClose() => plugin.Alert.Placing = false;

    public override void Draw()
    {
        var poetics = Plugin.ObjectTable.LocalPlayer != null ? Soils.Poetics() : 0;
        ImGui.TextUnformatted($"Poetics: {poetics:N0} / {Soils.PoeticsCap:N0}  ({poetics / Soils.PoeticsPrice} topsoil at {Soils.PoeticsPrice} each)");
        ImGui.Spacing();

        // Topsoil
        var names = Soils.Options.Select(o => o.SoilName).ToArray();
        var idx = Math.Clamp(Config.SoilIndex, 0, names.Length - 1);
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("Topsoil", ref idx, names, names.Length)) { Config.SoilIndex = idx; Config.Save(); }
        var soil = Soils.Options[idx];
        ImGui.TextDisabled($"Hismena: {Soils.PoeticsPrice} Poetics -> {soil.TokenName}; Bertana: {soil.TokenName} -> {soil.SoilName} (Idyllshire)");

        var blocker = plugin.Runner.Blocker();
        if (plugin.Runner.Busy) { if (ImGui.Button("Stop")) plugin.Runner.Stop(); }
        else
        {
            if (blocker != null) ImGui.BeginDisabled();
            if (ImGui.Button("Spend my Poetics now")) plugin.Runner.Start();
            if (blocker != null) { ImGui.EndDisabled(); ImGui.SameLine(); ImGui.TextDisabled(blocker); }

            var held = Soils.Count(Soils.ItemId(soil.TokenName));
            if (held > 0 && plugin.Runner.Blocker(tradeOnly: true) == null)
            {
                if (ImGui.Button($"Trade the {held} {soil.TokenName} I have")) plugin.Runner.Start(tradeOnly: true);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Skips Hismena: takes what's in your bag to Bertana.");
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Near-cap alert");
        var enabled = Config.AlertEnabled;
        if (ImGui.Checkbox("Show the alert", ref enabled)) { Config.AlertEnabled = enabled; Config.Save(); }

        var threshold = Config.AlertThreshold;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("Pop up at (Poetics)", ref threshold, 50, 100))
        {
            Config.AlertThreshold = Math.Clamp(threshold, Soils.PoeticsPrice, Soils.PoeticsCap);
            Config.Save();
        }

        var scale = Config.AlertScale;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderFloat("Alert size", ref scale, 0.8f, 2.5f, "%.1f")) { Config.AlertScale = scale; Config.Save(); }

        ImGui.Spacing();
        ImGui.TextUnformatted("Alert position");
        var placing = plugin.Alert.Placing;
        if (ImGui.Checkbox("Place the alert (drag it on screen)", ref placing)) plugin.Alert.Placing = placing;

        var pos = Config.AlertPosition ?? new Vector2(ImGui.GetMainViewport().Size.X / 2 - 180, 80);
        ImGui.SetNextItemWidth(200);
        if (ImGui.DragFloat2("X / Y (pixels)", ref pos, 1f, 0, 10000, "%.0f")) { Config.AlertPosition = pos; Config.Save(); }
        if (ImGui.Button("Reset to top centre")) { Config.AlertPosition = null; Config.Save(); }

        ImGui.Separator();
        ImGui.TextDisabled($"Remembered shop rows: {Config.ConfirmedRows.Count}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Forget")) { Config.ConfirmedRows.Clear(); Config.Save(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("The next run asks you to confirm each shop row again.");
    }
}
