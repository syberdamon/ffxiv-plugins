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

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Controller = new StanceController(Configuration);

        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open settings. /tsm on | off | mt | ot",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;
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
            default: ToggleConfigUi(); return;
        }

        Configuration.Save();
        ChatGui.Print($"[TSM] {(Configuration.Enabled ? "Enabled" : "Disabled")}, role: {(Configuration.Role == TankRole.MainTank ? "MT" : "OT")}");
    }

    public void ToggleConfigUi() => configWindow.Toggle();
}
