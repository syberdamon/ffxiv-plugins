using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MapClickNav.Windows;

namespace MapClickNav;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/mapnav";

    public Configuration Configuration { get; }
    public VNavIpc VNav { get; }
    public Navigator Navigator { get; }

    private readonly WindowSystem windowSystem = new("MapClickNav");
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        VNav = new VNavIpc();
        Navigator = new Navigator(Configuration, VNav);

        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(new StopWindow(Navigator));

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open settings. /mapnav stop | on | off",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfig;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfig;
        windowSystem.RemoveAllWindows();
        Navigator.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "stop": Navigator.StopTrip(); return;
            case "on": Configuration.Enabled = true; break;
            case "off": Configuration.Enabled = false; break;
            default: ToggleConfig(); return;
        }

        Configuration.Save();
        ChatGui.Print($"[MapClickNav] {(Configuration.Enabled ? "Enabled" : "Disabled")}");
    }

    private void ToggleConfig() => configWindow.Toggle();
}
