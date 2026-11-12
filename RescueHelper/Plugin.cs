using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace RescueHelper;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/rescue";

    public Configuration Configuration { get; }
    public DangerWatch Watch { get; }
    private readonly WindowSystem windows = new("RescueHelper");
    private readonly PanelWindow panel;
    private readonly SettingsWindow settings;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Watch = new DangerWatch(Configuration);
        panel = new PanelWindow(this);
        settings = new SettingsWindow(this);
        windows.AddWindow(panel);
        windows.AddWindow(settings);
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Rescue Helper settings. /rescue on | off | click | auto | panel",
        });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += settings.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += settings.Toggle;
    }

    public void ToggleSettings() => settings.Toggle();

    private void OnCommand(string cmd, string args)
    {
        var c = Configuration;
        switch (args.Trim().ToLowerInvariant())
        {
            case "on": c.Enabled = true; break;
            case "off": c.Enabled = false; break;
            case "click": c.Mode = RescueMode.Click; break;
            case "auto": c.Mode = RescueMode.Automatic; break;
            case "panel": c.ShowPanel = !c.ShowPanel; break;
            default: settings.Toggle(); return;
        }
        c.Save();
        ChatGui.Print($"[Rescue] {(c.Enabled ? "on" : "off")}, {(c.Mode == RescueMode.Click ? "click to rescue" : "automatic")}, panel {(c.ShowPanel ? "shown" : "hidden")}");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= settings.Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= settings.Toggle;
        windows.RemoveAllWindows();
        CommandManager.RemoveHandler(CommandName);
        Watch.Dispose();
    }
}
