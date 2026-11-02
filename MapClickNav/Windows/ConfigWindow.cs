using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MapClickNav.Windows;

public sealed class ConfigWindow : Window
{
    private static readonly string[] ModifierNames = Enum.GetNames<ModifierKey>();

    private readonly Configuration config;
    private readonly Navigator navigator;
    private readonly VNavIpc vnav;

    public ConfigWindow(Plugin plugin) : base("Map Click Nav - Options###MCNConfig")
    {
        config = plugin.Configuration;
        navigator = plugin.Navigator;
        vnav = plugin.VNav;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(360, 260), MaximumSize = new Vector2(800, 800) };
    }

    public override void Draw()
    {
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled)) { config.Enabled = enabled; config.Save(); }

        var mod = (int)config.Modifier;
        if (ImGui.Combo("Modifier + left-click", ref mod, ModifierNames, ModifierNames.Length))
        {
            config.Modifier = (ModifierKey)mod;
            config.Save();
        }

        ImGui.Separator();
        var crossZone = config.CrossZoneTeleport;
        if (ImGui.Checkbox("Clicking another zone's map teleports there first", ref crossZone)) { config.CrossZoneTeleport = crossZone; config.Save(); }

        var tpFaster = config.TeleportIfFaster;
        if (ImGui.Checkbox("In my zone, teleport when it's faster than traveling", ref tpFaster)) { config.TeleportIfFaster = tpFaster; config.Save(); }

        if (config.TeleportIfFaster)
        {
            var margin = config.TeleportMargin;
            if (ImGui.SliderInt("Teleport only if it saves (yalms)", ref margin, 0, 1000)) { config.TeleportMargin = margin; config.Save(); }
        }

        ImGui.Separator();
        var autoMount = config.AutoMount;
        if (ImGui.Checkbox("Mount with Mount Roulette before traveling", ref autoMount)) { config.AutoMount = autoMount; config.Save(); }

        if (config.AutoMount)
        {
            var ground = config.MountForGroundTravel;
            if (ImGui.Checkbox("Also mount in zones where I can't fly", ref ground)) { config.MountForGroundTravel = ground; config.Save(); }

            var timeout = config.MountTimeoutSeconds;
            if (ImGui.SliderInt("Mount timeout (s)", ref timeout, 3, 15)) { config.MountTimeoutSeconds = timeout; config.Save(); }
        }

        var chat = config.ChatFeedback;
        if (ImGui.Checkbox("Print status to chat", ref chat)) { config.ChatFeedback = chat; config.Save(); }

        ImGui.Separator();
        ImGui.TextUnformatted($"vnavmesh: {(vnav.IsReady ? "ready" : "not ready (installed? mesh still building?)")}");
        ImGui.TextUnformatted($"Trip: {navigator.State} - {navigator.StatusText}");
    }
}
