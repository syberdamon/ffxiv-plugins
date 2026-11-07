using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace MateriaManager;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/materia";

    public Configuration Configuration { get; }
    public Storage Storage { get; }
    public Transmuter Transmuter { get; }
    private readonly WindowSystem windows = new("MateriaManager");
    private readonly MainWindow main;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Storage = new Storage(Configuration);
        Transmuter = new Transmuter(Configuration);
        main = new MainWindow(this);
        windows.AddWindow(main);
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Materia Manager. /materia store on|off | saddlebag | retainer | transmute | stop",
        });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += main.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += main.Toggle;
    }

    private void OnCommand(string cmd, string args)
    {
        var c = Configuration;
        switch (args.Trim().ToLowerInvariant())
        {
            case "store on": c.AutoStore = true; break;
            case "store off": c.AutoStore = false; break;
            case "saddlebag": c.StoreTo = StoreTo.Saddlebag; break;
            case "retainer": c.StoreTo = StoreTo.Retainer; break;
            case "transmute": Transmuter.Start(); return;
            case "stop": Transmuter.Stop(); return;
            default: main.Toggle(); return;
        }
        c.Save();
        ChatGui.Print($"[Materia] Auto-store {(c.AutoStore ? "on" : "off")}, to the {(c.StoreTo == StoreTo.Saddlebag ? "saddlebag" : "retainer")}");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= main.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= main.Toggle;
        windows.RemoveAllWindows();
        CommandManager.RemoveHandler(CommandName);
        Storage.Dispose();
        Transmuter.Dispose();
    }
}
