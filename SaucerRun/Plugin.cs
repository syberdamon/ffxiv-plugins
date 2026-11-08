using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SaucerRun.Windows;

namespace SaucerRun;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;

    private const string CommandName = "/saucer";

    public Configuration Configuration { get; }
    public Tracker Tracker { get; }
    public Runner Runner { get; }

    private readonly WindowSystem windows = new("SaucerRun");
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Tracker = new Tracker(Configuration);
        Runner = new Runner(Configuration, Tracker);
        configWindow = new ConfigWindow(this);
        windows.AddWindow(configWindow);
        windows.AddWindow(new ButtonWindow(this));
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Saucer Run. /saucer (settings) | run | stop",
        });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfig;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
    }

    public void ToggleConfig() => configWindow.Toggle();

    private void OnCommand(string cmd, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "run": Runner.Start(); return;
            case "stop": Runner.Stop(); return;
            default: ToggleConfig(); return;
        }
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfig;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        windows.RemoveAllWindows();
        CommandManager.RemoveHandler(CommandName);
        Runner.Dispose();
        Tracker.Dispose();
    }
}
