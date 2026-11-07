using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using RetainerGear.Windows;

namespace RetainerGear;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/rgear";

    public Configuration Configuration { get; }
    public RetainerTracker Tracker { get; }
    public GearSources Sources { get; }
    public Prices Prices { get; }

    private readonly WindowSystem windows = new("RetainerGear");
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Sources = new GearSources(Configuration);
        Tracker = new RetainerTracker(Configuration);
        Prices = new Prices();

        mainWindow = new MainWindow(this);
        windows.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo((_, _) => mainWindow.Toggle())
        {
            HelpMessage = "Retainer gear: what each retainer still needs, and a shopping list.",
        });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += mainWindow.Toggle;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= mainWindow.Toggle;
        windows.RemoveAllWindows();
        CommandManager.RemoveHandler(CommandName);
        Tracker.Dispose();
        Prices.Dispose();
    }
}
