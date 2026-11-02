using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MitPlanner.Windows;

namespace MitPlanner;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/mitplan";

    public Configuration Configuration { get; }
    public TimelineRunner Runner { get; }
    public PlanTracker Tracker { get; }
    public Targeting Targeting { get; }

    /// <summary>Show the callout window's background so it can be dragged into place.</summary>
    public bool ShowCalloutPlacement { get; set; }

    private readonly WindowSystem windowSystem = new("MitPlanner");
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Migrate();
        Runner = new TimelineRunner();
        Targeting = new Targeting(Runner);
        Tracker = new PlanTracker(Configuration, Runner, Targeting);

        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(new TimelineWindow(this));
        windowSystem.AddWindow(new CalloutWindow(this));

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open settings. /mitplan mt | ot | on | off (plan) | auto on | auto off | preview | stop | timeline",
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
        Tracker.Dispose();
        Targeting.Dispose();
        Runner.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "preview":
                Runner.StartPreview(Runner.Fight ?? Fights.All[0]);
                return;
            case "stop":
                Runner.Stop();
                return;
            case "on":
            case "off":
                Configuration.PlanEnabled = args.Trim().ToLowerInvariant() == "on";
                Configuration.Save();
                ChatGui.Print($"[MitPlanner] Plan {(Configuration.PlanEnabled ? "ON" : "OFF")}");
                return;
            case "auto on":
            case "auto off":
                Configuration.AllowAutoUse = args.Trim().ToLowerInvariant() == "auto on";
                Configuration.Save();
                ChatGui.Print($"[MitPlanner] Auto-use {(Configuration.AllowAutoUse ? "ON" : "OFF")}");
                return;
            case "mt":
            case "ot":
                Configuration.Role = args.Trim().ToLowerInvariant() == "ot" ? TankRole.OffTank : TankRole.MainTank;
                Configuration.Save();
                ChatGui.Print($"[MitPlanner] Role: {Configuration.RoleCode(Configuration.Role)}");
                return;
            case "timeline":
                Configuration.ShowTimeline = !Configuration.ShowTimeline;
                Configuration.Save();
                return;
            default:
                ToggleConfig();
                return;
        }
    }

    private void ToggleConfig() => configWindow.Toggle();
}
