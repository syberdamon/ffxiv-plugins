using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace CooldownGrid.Windows;

/// <summary>
/// A small floating "Plugins" button that stays up during cutscenes (Cooldown Grid opts out of
/// Dalamud's cutscene hiding and hides its own grids instead). Click: a menu to open the plugin
/// installer (installed / updates), Dalamud's settings, or any loaded plugin's window or settings,
/// so you don't need to remember its text command.
/// </summary>
public sealed class PluginsButton : Window
{
    private readonly Plugin plugin;

    public PluginsButton(Plugin plugin) : base("Plugins###CooldownGridPluginsButton")
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    private Configuration C => plugin.Configuration;

    public override bool DrawConditions() =>
        C.ShowPluginsButton && (!C.PluginsButtonCutscenesOnly || Plugin.PluginInterface.UiBuilder.CutsceneActive);

    public override void PreDraw()
    {
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoFocusOnAppearing;
        if (C.PluginsButtonLocked) Flags |= ImGuiWindowFlags.NoMove;
        ShowCloseButton = false;
        ImGui.SetNextWindowPos(new Vector2(20, 220), ImGuiCond.FirstUseEver);
    }

    public override void Draw()
    {
        if (ImGui.Button("Plugins")) ImGui.OpenPopup("##pluginsmenu");
        if (ImGui.IsItemHovered() && !ImGui.IsPopupOpen("##pluginsmenu"))
            ImGui.SetTooltip("Plugin installer, updates, Dalamud settings, or any plugin's window.\n" +
                             "Right-click: Cooldown Grid settings (hide / lock this button).");
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) plugin.ToggleConfigPublic();
        if (!ImGui.BeginPopup("##pluginsmenu")) return;

        var pi = Plugin.PluginInterface;
        if (ImGui.MenuItem("Installed plugins")) pi.OpenPluginInstallerTo(PluginInstallerOpenKind.InstalledPlugins);
        if (ImGui.MenuItem("Updates")) pi.OpenPluginInstallerTo(PluginInstallerOpenKind.UpdateablePlugins);
        if (ImGui.MenuItem("All plugins (browse)")) pi.OpenPluginInstallerTo(PluginInstallerOpenKind.AllPlugins);
        if (ImGui.MenuItem("Dalamud settings")) pi.OpenDalamudSettingsTo(SettingsOpenKind.General);
        ImGui.Separator();

        var list = pi.InstalledPlugins.Where(p => p.IsLoaded && (p.HasMainUi || p.HasConfigUi)).OrderBy(p => p.Name).ToList();
        if (list.Count == 0) ImGui.TextDisabled("No plugin windows to open.");
        foreach (var p in list)
        {
            if (p.HasMainUi && p.HasConfigUi)
            {
                if (!ImGui.BeginMenu($"{p.Name}##{p.InternalName}")) continue;
                if (ImGui.MenuItem("Open")) p.OpenMainUi();
                if (ImGui.MenuItem("Settings")) p.OpenConfigUi();
                ImGui.EndMenu();
            }
            else if (ImGui.MenuItem($"{p.Name}{(p.HasMainUi ? "" : " (settings)")}##{p.InternalName}"))
            {
                if (p.HasMainUi) p.OpenMainUi(); else p.OpenConfigUi();
            }
        }
        if (pi.UiBuilder.CutsceneActive)
        {
            ImGui.Separator();
            ImGui.TextDisabled("In a cutscene, other plugins' windows stay hidden until it ends,\n" +
                               "unless \"Hide plugin UI during cutscenes\" is off (Dalamud settings, Look & Feel).");
        }
        ImGui.EndPopup();
    }
}
