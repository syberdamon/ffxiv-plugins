using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using TankStanceManager.Windows;

namespace TankStanceManager;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/tsm";

    public Configuration Configuration { get; }
    public StanceController Controller { get; }

    private readonly WindowSystem windowSystem = new("TankStanceManager");
    private readonly ConfigWindow configWindow;
    private readonly ControlWindow controlWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Controller = new StanceController(Configuration);

        configWindow = new ConfigWindow(this);
        controlWindow = new ControlWindow(this) { IsOpen = Configuration.ShowControlWindow };
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(controlWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Show/hide the control panel. /tsm options | on | off | mt | ot",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleOptions;
        PluginInterface.UiBuilder.OpenMainUi += ToggleControls;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleOptions;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleControls;
        windowSystem.RemoveAllWindows();
        Controller.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on": Configuration.Enabled = true; break;
            case "off": Configuration.Enabled = false; break;
            case "mt": Configuration.Role = TankRole.MainTank; break;
            case "ot": Configuration.Role = TankRole.OffTank; break;
            case "options": case "config": ToggleOptions(); return;
            default: ToggleControls(); return;
        }

        Configuration.Save();
        ChatGui.Print($"[TSM] {(Configuration.Enabled ? "Enabled" : "Disabled")}, role: {(Configuration.Role == TankRole.MainTank ? "MT" : "OT")}");
    }

    public void ToggleOptions() => configWindow.Toggle();
    public void ToggleControls() => controlWindow.Toggle();
}
