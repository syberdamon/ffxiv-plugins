using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MaterialStash.Core;
using MaterialStash.Windows;

namespace MaterialStash;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/stash";

    public Configuration Configuration { get; }
    public Tracker Tracker { get; }
    public Planner Planner { get; }
    public Runner Runner { get; }
    public AutoRetainerIpc AutoRetainer { get; } = new();
    public AllaganIpc Allagan { get; } = new();

    private readonly WindowSystem windows = new("MaterialStash");
    private readonly MainWindow main;
    private readonly BellOverlay bell;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Tracker = new Tracker(Configuration);
        Planner = new Planner(Configuration, Tracker);
        Runner = new Runner(Configuration, Tracker, Planner, AutoRetainer);
        main = new MainWindow(this);
        bell = new BellOverlay(this);
        windows.AddWindow(main);
        windows.AddWindow(bell);
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Material Stash. /stash (settings) | deposit (at the bell) | scan (record every retainer) | stop",
        });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMain;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMain;
    }

    public void ToggleMain() => main.Toggle();

    private void OnCommand(string cmd, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "deposit": Runner.Start(scan: false); Report(); return;
            case "scan": Runner.Start(scan: true); Report(); return;
            case "stop": Runner.Stop(); return;
            default: main.Toggle(); return;
        }
    }

    private void Report()
    {
        if (!Runner.Busy && Runner.Status.Length > 0) ChatGui.Print($"[Stash] {Runner.Status}");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMain;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMain;
        windows.RemoveAllWindows();
        CommandManager.RemoveHandler(CommandName);
        Runner.Dispose();
        Tracker.Dispose();
    }
}
