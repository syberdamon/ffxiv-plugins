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
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;

    private const string CommandName = "/stash";

    public Configuration Configuration { get; }
    public Tracker Tracker { get; }
    public Planner Planner { get; }
    public Runner Runner { get; }
    public Director Director { get; }
    public AfterAutoRetainer AfterAr { get; private set; } = null!;
    public SaddleMover Saddle { get; }
    public Market Market { get; } = new();
    public Selling Selling { get; }
    public Cleanup Cleanup { get; }
    public Wardrobe Wardrobe { get; }
    public Storeroom Storeroom { get; }
    public BellWalker Bell { get; } = new();
    public AutoRetainerIpc AutoRetainer { get; } = new();
    public AllaganIpc Allagan { get; } = new();
    public ScripGoals Scrips { get; private set; } = null!;
    public IceBreak IceBreak { get; private set; } = null!;
    public ArtisanWatch ArtisanWatch { get; private set; } = null!;

    private readonly WindowSystem windows = new("MaterialStash");
    private readonly MainWindow main;
    private readonly BellOverlay bell;
    private readonly SaddleOverlay saddle;
    private readonly VentureWindow ventures;
    private readonly SellHighlight sellHighlight;
    private readonly ScripWindow scripWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.EnsureGroups();
        Configuration.Save();
        Tracker = new Tracker(Configuration);
        Planner = new Planner(Configuration, Tracker);
        Director = new Director(Configuration, Tracker);
        Selling = new Selling(Configuration, Tracker, Market, Director);
        Director.Selling = Selling;
        Runner = new Runner(Configuration, Tracker, Planner, AutoRetainer, Selling);
        Cleanup = new Cleanup(Configuration, Tracker, Market);
        Runner.Cleanup = Cleanup;
        Wardrobe = new Wardrobe(Configuration, Tracker, Cleanup);
        Runner.Wardrobe = Wardrobe;
        Storeroom = new Storeroom(Configuration);
        Runner.Storeroom = Storeroom;
        Saddle = new SaddleMover(Configuration);
        AfterAr = new AfterAutoRetainer(this);
        Scrips = new ScripGoals(this);
        IceBreak = new IceBreak(this);
        ArtisanWatch = new ArtisanWatch(this);
        main = new MainWindow(this);
        bell = new BellOverlay(this);
        saddle = new SaddleOverlay(this);
        ventures = new VentureWindow(this);
        sellHighlight = new SellHighlight(this);
        scripWindow = new ScripWindow(this);
        windows.AddWindow(main);
        windows.AddWindow(bell);
        windows.AddWindow(saddle);
        windows.AddWindow(ventures);
        windows.AddWindow(sellHighlight);
        windows.AddWindow(scripWindow);
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Material Stash. /stash (settings) | deposit (at the bell) | tidy (sort items between retainers) | sell (list the to-sell items) | clean (carry out the approved clean-up) | desynth (desynthesize the approved items in your bags) | looks (store approved looks at an inn) | furnish (furnishings off the retainers, at the bell) | storeroom (furnishings in your bags into the house storeroom) | bell (go to the nearest summoning bell) | scan (record every retainer) | saddle in|out (mats into / out of the open saddlebag) | scrips (work through the scrip goals) | resume (after buying on the market board) | icebreak (toggle: pause ICE for retainers) | icebreak now | stop",
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
            case "tidy": Runner.Start(Runner.RunMode.Tidy); Report(); return;
            case "sell": Runner.Start(Runner.RunMode.Sell); Report(); return;
            case "clean": Runner.Start(Runner.RunMode.Cleanup); Report(); return;
            case "furnish": Runner.Start(Runner.RunMode.Furnish); Report(); return;
            case "bell": Bell.Start(); if (!Bell.Busy) ChatGui.Print($"[Stash] {Bell.Status}"); return;
            case "storeroom": Storeroom.Start(); if (!Storeroom.Busy) ChatGui.Print($"[Stash] {Storeroom.Status}"); return;
            case "desynth": Cleanup.StartDesynth(); if (!Cleanup.Desynthing) ChatGui.Print($"[Stash] {Cleanup.Status}"); return;
            case "looks": Wardrobe.Start(); if (!Wardrobe.Busy) ChatGui.Print($"[Stash] {Wardrobe.Status}"); return;
            case "ventures": Configuration.ShowVentureWindow = !Configuration.ShowVentureWindow; Configuration.Save(); return;
            case "saddle in": Saddle.Start(SaddleMover.Direction.ToSaddlebag); if (!Saddle.Busy) ChatGui.Print($"[Stash] {Saddle.Status}"); return;
            case "saddle out": Saddle.Start(SaddleMover.Direction.ToBags); if (!Saddle.Busy) ChatGui.Print($"[Stash] {Saddle.Status}"); return;
            case "scrips": Scrips.Start(); if (!Scrips.Busy && Scrips.Status.Length > 0) ChatGui.Print($"[Stash] {Scrips.Status}"); return;
            case "resume": Scrips.Resume(); return;
            case "icebreak":
                Configuration.IceBreakEnabled = !Configuration.IceBreakEnabled; Configuration.Save();
                ChatGui.Print($"[Stash] ICE break for retainers: {(Configuration.IceBreakEnabled ? "on" : "off")}.");
                if (!Configuration.IceBreakEnabled) IceBreak.Stop("cancelled (turned off)");
                return;
            case "icebreak now":
                var why = IceBreak.StartNow();
                if (why.Length > 0) ChatGui.Print($"[Stash] ICE break: {why}");
                return;
            case "stop": IceBreak.Stop(); Scrips.Stop(); Runner.Stop(); Saddle.Stop(); Cleanup.StopAll(); Wardrobe.Stop(); Storeroom.Stop(); Bell.Stop(); return;
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
        ArtisanWatch.Dispose();
        IceBreak.Dispose();
        AfterAr.Dispose();
        Scrips.Dispose();
        Runner.Dispose();
        Wardrobe.Dispose();
        Storeroom.Dispose();
        Bell.Dispose();
        Cleanup.Dispose();
        Director.Dispose();
        Selling.Dispose();
        Market.Dispose();
        Saddle.Dispose();
        Tracker.Dispose();
    }
}
