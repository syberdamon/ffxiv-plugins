using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;

namespace PoeticsSpender.Windows;

/// <summary>
/// Pops up when Poetics reach the threshold. Clicking it runs the purchase.
/// While "Place the alert" is on (settings), it shows a sample you can drag; the spot is saved.
/// </summary>
public sealed class AlertWindow : Window
{
    private static readonly Vector4 Gold = new(1f, 0.82f, 0.35f, 1f);

    private readonly Plugin plugin;
    private bool dismissed;
    private int poetics;
    private DateTime nextPoll = DateTime.MinValue;

    public bool Placing { get; set; }

    public AlertWindow(Plugin plugin) : base("Poetics Alert###PoeticsAlert")
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    private Configuration Config => plugin.Configuration;

    public override bool DrawConditions()
    {
        if (Placing) return true;
        if (DateTime.UtcNow >= nextPoll)
        {
            poetics = Plugin.ObjectTable.LocalPlayer != null ? Soils.Poetics() : 0;
            nextPoll = DateTime.UtcNow.AddSeconds(1);
        }
        if (poetics < Config.AlertThreshold) dismissed = false; // re-arm once you've spent them
        return Config.AlertEnabled && !dismissed && poetics >= Config.AlertThreshold && !plugin.Runner.Busy &&
               !Plugin.Condition[ConditionFlag.InCombat] && !Plugin.Condition[ConditionFlag.BoundByDuty] &&
               !Plugin.Condition[ConditionFlag.BetweenAreas];
    }

    public override void PreDraw()
    {
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoSavedSettings;
        ShowCloseButton = false;

        var pos = Config.AlertPosition ?? new Vector2(ImGui.GetMainViewport().Size.X / 2 - 180, 80);
        if (Placing)
        {
            ImGui.SetNextWindowPos(pos, ImGuiCond.Appearing);
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
            ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        }
    }

    public override void Draw()
    {
        ImGui.SetWindowFontScale(Config.AlertScale);
        var soil = Soils.Options[Math.Clamp(Config.SoilIndex, 0, Soils.Options.Count - 1)];
        var shown = Placing ? Math.Max(poetics, Config.AlertThreshold) : poetics;

        ImGui.TextColored(Gold, $"Poetics {shown:N0} / {Soils.PoeticsCap:N0}");
        ImGui.SameLine();
        if (!Placing && ImGui.SmallButton("x")) dismissed = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Hide until you've spent them");

        var count = shown / Soils.PoeticsPrice;
        if (ImGui.Button($"Buy {count} x {soil.SoilName}") && !Placing)
            plugin.Runner.Start();

        if (Placing)
        {
            ImGui.TextDisabled("Drag me where you want the alert, then untick 'Place the alert'.");
            var p = ImGui.GetWindowPos();
            if (Config.AlertPosition != p)
            {
                Config.AlertPosition = p;
                if (!ImGui.IsMouseDragging(ImGuiMouseButton.Left)) Config.Save();
            }
        }
        ImGui.SetWindowFontScale(1f);
    }
}
