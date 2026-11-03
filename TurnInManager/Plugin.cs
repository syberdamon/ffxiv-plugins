using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using TurnInManager.Core;
using TurnInManager.GrandCompany;
using TurnInManager.Windows;

namespace TurnInManager;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/turnin";

    public Configuration Configuration { get; }
    public GcRunner GcRunner { get; }

    private readonly WindowSystem windowSystem = new("TurnInManager");
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        GcRunner = new GcRunner(Configuration, new VNavIpc(), new LifestreamIpc());

        mainWindow = new MainWindow(this);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(new StatusWindow(this));

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open. /turnin gc (run Grand Company) | buy (seal shop only) | stop | dump <window>",
        });
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        windowSystem.RemoveAllWindows();
        GcRunner.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2);
        switch (parts[0].ToLowerInvariant())
        {
            case "gc": GcRunner.Start(); break;
            case "buy": GcRunner.Start(buyOnly: true); break;
            case "stop": GcRunner.Stop(); break;
            case "dump": GameUi.Dump(parts.Length > 1 ? parts[1] : "GrandCompanySupplyList"); ChatGui.Print("[TurnIn] Written to /xllog."); break;
            default: mainWindow.Toggle(); break;
        }
    }
}
