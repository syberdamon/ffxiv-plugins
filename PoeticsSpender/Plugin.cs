using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PoeticsSpender.Windows;

namespace PoeticsSpender;

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

    private const string CommandName = "/poetics";

    public Configuration Configuration { get; }
    public Runner Runner { get; }
    public AlertWindow Alert { get; }

    private readonly WindowSystem windowSystem = new("PoeticsSpender");
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Runner = new Runner(Configuration, new VNavIpc());

        configWindow = new ConfigWindow(this);
        Alert = new AlertWindow(this);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(Alert);
        windowSystem.AddWindow(new StatusWindow(this));

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Settings. /poetics run | trade (Ore/Shell you hold -> topsoil) | stop | dump <addon>",
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
        Runner.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2);
        switch (parts[0].ToLowerInvariant())
        {
            case "run": Runner.Start(); break;
            case "trade": Runner.Start(tradeOnly: true); break;
            case "stop": Runner.Stop(); break;
            case "dump":
                GameUi.Dump(parts.Length > 1 ? parts[1] : "ShopExchangeCurrency");
                ChatGui.Print("[Poetics] Written to /xllog.");
                break;
            default: ToggleConfig(); break;
        }
    }

    private void ToggleConfig() => configWindow.Toggle();
}
