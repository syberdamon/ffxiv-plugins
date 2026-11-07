using System;
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
    public BusterGuard Guard { get; }
    public TrashMode Trash { get; }

    /// <summary>Show the callout window's background so it can be dragged into place.</summary>
    public bool ShowCalloutPlacement { get; set; }

    private readonly WindowSystem windowSystem = new("MitPlanner");
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Migrate();
        Runner = new TimelineRunner();
        Targeting = new Targeting(Runner, Configuration);
        Tracker = new PlanTracker(Configuration, Runner, Targeting);
        Guard = new BusterGuard(Configuration, Runner, Targeting, Tracker);
        Trash = new TrashMode(Configuration, Runner);
        RegisterIpc();

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

    // ---- IPC for other plugins (Cooldown Grid's healer alerts).
    // "MitPlanner.NextMechanicIn"(type): seconds until the next timeline line of that kind, or
    // float.MaxValue. type 1 = tank buster (incl. shared), 2 = raidwide (incl. stacks).
    private Dalamud.Plugin.Ipc.ICallGateProvider<int, float>? nextMechanicIpc;

    private void RegisterIpc()
    {
        nextMechanicIpc = PluginInterface.GetIpcProvider<int, float>("MitPlanner.NextMechanicIn");
        nextMechanicIpc.RegisterFunc(NextMechanicIn);
    }

    private float NextMechanicIn(int type)
    {
        if (!Runner.Running || Runner.Preview || Runner.Fight is not { } fight) return float.MaxValue;
        var now = Runner.Now;
        foreach (var e in Runner.Upcoming(60, e => e.Time >= now))
        {
            var t = Configuration.TagFor(fight, e.Label);
            var match = type == 1 ? t is MechanicType.TankBuster or MechanicType.SharedTankBuster
                      : type == 2 && t is MechanicType.Raidwide or MechanicType.Stack;
            if (match) return e.Time - now;
        }
        return float.MaxValue;
    }

    public void Dispose()
    {
        nextMechanicIpc?.UnregisterFunc();
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfig;
        windowSystem.RemoveAllWindows();
        Trash.Dispose();
        Guard.Dispose();
        Tracker.Dispose();
        Targeting.Dispose();
        Runner.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "trash on":
            case "trash off":
                Configuration.Trash.Enabled = args.Trim().EndsWith("on", StringComparison.OrdinalIgnoreCase);
                Configuration.Save();
                ChatGui.Print($"[MitPlanner] Trash mode {(Configuration.Trash.Enabled ? "on" : "off")}.");
                return;
            case "preview":
                if (Runner.Fight != null) Runner.StartPreview(Runner.Fight);
                else ChatGui.Print("[MitPlanner] No timeline for this zone. Pick a fight under Display > Preview in /mitplan.");
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
