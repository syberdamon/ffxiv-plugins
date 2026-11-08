using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using TurnInManager.Core;
using Dalamud.Plugin.Ipc;
using TurnInManager.GrandCompany;
using TurnInManager.Scrips;
using TurnInManager.Windows;

namespace TurnInManager;

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

    private const string CommandName = "/turnin";

    public Configuration Configuration { get; }
    public GcRunner GcRunner { get; }
    public ScripRunner ScripRunner { get; }

    // IPC for Material Stash's scrip goals (start a turn-in + buy run, watch it).
    private readonly ICallGateProvider<uint, int, string> ipcStart;
    private readonly ICallGateProvider<bool> ipcBusy;
    private readonly ICallGateProvider<bool> ipcFailed;
    private readonly ICallGateProvider<string> ipcStatus;
    private readonly ICallGateProvider<int> ipcBought;
    private readonly ICallGateProvider<object> ipcStop;

    private readonly WindowSystem windowSystem = new("TurnInManager");
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        // Older versions duplicated purchase rows on every load: keep the first row per item.
        var dedup = Configuration.Purchases.GroupBy(r => r.ItemId).Select(g => g.First()).ToList();
        if (dedup.Count != Configuration.Purchases.Count) { Configuration.Purchases = dedup; Configuration.Save(); }
        GcRunner = new GcRunner(Configuration, new VNavIpc(), new LifestreamIpc());
        ScripRunner = new ScripRunner(Configuration, new VNavIpc(), new LifestreamIpc());

        ipcStart = PluginInterface.GetIpcProvider<uint, int, string>("TurnInManager.Scrips.Start");
        ipcStart.RegisterFunc((item, count) => ScripRunner.Start(item, count));
        ipcBusy = PluginInterface.GetIpcProvider<bool>("TurnInManager.Scrips.IsBusy");
        ipcBusy.RegisterFunc(() => ScripRunner.Busy);
        ipcFailed = PluginInterface.GetIpcProvider<bool>("TurnInManager.Scrips.Failed");
        ipcFailed.RegisterFunc(() => ScripRunner.Failed);
        ipcStatus = PluginInterface.GetIpcProvider<string>("TurnInManager.Scrips.Status");
        ipcStatus.RegisterFunc(() => ScripRunner.Status);
        ipcBought = PluginInterface.GetIpcProvider<int>("TurnInManager.Scrips.Bought");
        ipcBought.RegisterFunc(() => ScripRunner.Bought);
        ipcStop = PluginInterface.GetIpcProvider<object>("TurnInManager.Scrips.Stop");
        ipcStop.RegisterAction(() => ScripRunner.Stop());

        mainWindow = new MainWindow(this);
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(new StatusWindow(this));

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open. /turnin gc (run Grand Company) | buy (seal shop only) | strip (retrieve materia only) | scrips <count> <item> (turn in collectables, buy the item) | stop | dump <window>",
        });
        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        windowSystem.RemoveAllWindows();
        GcRunner.Dispose();
        ipcStart.UnregisterFunc(); ipcBusy.UnregisterFunc(); ipcFailed.UnregisterFunc();
        ipcStatus.UnregisterFunc(); ipcBought.UnregisterFunc(); ipcStop.UnregisterAction();
        ScripRunner.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2);
        switch (parts[0].ToLowerInvariant())
        {
            case "gc": GcRunner.Start(); break;
            case "buy": GcRunner.Start(GcMode.BuyOnly); break;
            case "strip": GcRunner.Start(GcMode.StripOnly); break;
            case "scrips": StartScrips(parts.Length > 1 ? parts[1] : ""); break;
            case "stop": GcRunner.Stop(); ScripRunner.Stop(); break;
            case "dump": GameUi.Dump(parts.Length > 1 ? parts[1] : "GrandCompanySupplyList"); ChatGui.Print("[TurnIn] Written to /xllog."); break;
            default: mainWindow.Toggle(); break;
        }
    }

    /// <summary>"/turnin scrips 20 Mason's Abrasive": turn in collectables and buy up to 20.</summary>
    private void StartScrips(string args)
    {
        var p = args.Trim().Split(' ', 2);
        if (p.Length < 2 || !int.TryParse(p[0], out var n))
        {
            ChatGui.Print("[TurnIn] /turnin scrips <count> <item name>, e.g. /turnin scrips 20 Mason's Abrasive");
            return;
        }
        var name = p[1].Trim();
        var hit = Syberdamon.Shared.ScripData.Shop(DataManager).Values.FirstOrDefault(e => e.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
        if (hit == null) { ChatGui.Print($"[TurnIn] \"{name}\" isn't sold for scrips."); return; }
        var why = ScripRunner.Start(hit.ItemId, n);
        if (why.Length > 0) ChatGui.Print($"[TurnIn] {why}");
    }
}
