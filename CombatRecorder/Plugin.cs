using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using CombatRecorder.Windows;

namespace CombatRecorder;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/crec";

    public Configuration Configuration { get; }
    public Recorder Recorder { get; }
    public Meter Meter { get; }

    private readonly WindowSystem windowSystem = new("CombatRecorder");
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Recorder = new Recorder(Configuration);
        Meter = new Meter(Configuration, Recorder);
        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(new MeterWindow(this));

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Settings. /crec mark <note> (flag this moment for review) | on | off | stop (close the current file) | meter (show/hide the DPS meter) | reset (clear the meter)",
        });
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= configWindow.Toggle;
        windowSystem.RemoveAllWindows();
        Meter.Dispose();
        Recorder.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    public void ToggleConfig() => configWindow.Toggle();

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2);
        switch (parts[0].ToLowerInvariant())
        {
            case "mark": Recorder.Mark(parts.Length > 1 ? parts[1] : "mark"); break;
            case "on": Configuration.Enabled = true; Configuration.Save(); ChatGui.Print("[CombatRecorder] On."); break;
            case "off": Configuration.Enabled = false; Configuration.Save(); Recorder.Close("turned off"); ChatGui.Print("[CombatRecorder] Off."); break;
            case "stop": Recorder.Close("stopped"); break;
            case "meter": Configuration.ShowMeter = !Configuration.ShowMeter; Configuration.Save(); break;
            case "reset": Meter.Reset(); break;
            default: configWindow.Toggle(); break;
        }
    }
}
