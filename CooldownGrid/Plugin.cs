using System;
using System.Collections.Generic;
using System.Linq;
using CooldownGrid.Windows;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CooldownGrid;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/cdgrid";

    public Configuration Configuration { get; }

    private readonly WindowSystem windowSystem = new("CooldownGrid");
    private readonly ConfigWindow configWindow;
    private readonly Dictionary<Guid, GroupWindow> groupWindows = new();

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.EnsureDefaults();
        Configuration.Migrate();
        Configuration.Save();

        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);
        SyncGroupWindows();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open settings. /cdgrid lock | unlock | show | hide",
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
        CommandManager.RemoveHandler(CommandName);
    }

    /// <summary>Add a window for each new group and remove windows for deleted ones.</summary>
    public void SyncGroupWindows()
    {
        foreach (var id in groupWindows.Keys.Where(id => Configuration.Groups.All(g => g.Id != id)).ToList())
        {
            windowSystem.RemoveWindow(groupWindows[id]);
            groupWindows.Remove(id);
        }

        foreach (var g in Configuration.Groups.Where(g => !groupWindows.ContainsKey(g.Id)))
        {
            var w = new GroupWindow(this, g);
            groupWindows[g.Id] = w;
            windowSystem.AddWindow(w);
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "lock": Configuration.Groups.ForEach(g => g.Locked = true); break;
            case "unlock": Configuration.Groups.ForEach(g => g.Locked = false); break;
            case "show": Configuration.Groups.ForEach(g => g.Visible = true); break;
            case "hide": Configuration.Groups.ForEach(g => g.Visible = false); break;
            default: ToggleConfig(); return;
        }
        Configuration.Save();
        ChatGui.Print($"[CooldownGrid] All groups: {args.Trim().ToLowerInvariant()}");
    }

    private void ToggleConfig() => configWindow.Toggle();
}
