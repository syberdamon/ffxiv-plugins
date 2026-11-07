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
        Configuration.Migrate();
        Storage = new Storage(Configuration);
        Transmuter = new Transmuter(Configuration);
        main = new MainWindow(this);
        windows.AddWindow(main);
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Materia Manager. /materia store on|off | pull (all from saddlebag) | pull 11-12 (by grade) | balance | release | transmute | stop",
        });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += main.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += main.Toggle;
    }

    private void OnCommand(string cmd, string args)
    {
        var c = Configuration;
        var a = args.Trim().ToLowerInvariant();
        // "/materia pull 11-12" or "/materia pull 11" (that grade and up)
        if (a.StartsWith("pull ") && a.Length > 5)
        {
            var parts = a[5..].Split('-', System.StringSplitOptions.TrimEntries);
            if (int.TryParse(parts[0], out var from))
            {
                var to = parts.Length > 1 && int.TryParse(parts[1], out var t) ? t : 12;
                Storage.PullGrades(System.Math.Clamp(from, 1, 12), System.Math.Clamp(to, 1, 12));
                return;
            }
        }
        switch (a)
        {
            case "store on": c.AutoStoreSaddlebag = true; break;
            case "store off": c.AutoStoreSaddlebag = false; break;
            case "pull": Storage.PullAllFromSaddlebag(); return;
            case "balance": Storage.BalanceNow(); return;
            case "release": Storage.Release(); return;
            case "transmute": Transmuter.Start(); return;
            case "stop": Transmuter.Stop(); Storage.Stop(); return;
            default: main.Toggle(); return;
        }
        c.Save();
        ChatGui.Print($"[Materia] Auto-store to the saddlebag {(c.AutoStoreSaddlebag ? "on" : "off")}");
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
