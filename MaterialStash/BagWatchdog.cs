using System;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MaterialStash.Core;

namespace MaterialStash;

/// <summary>
/// Your own inventory window left on screen with a dead X (no flicker: nothing reopens it, the game has just lost
/// track of it) after a retainer window was closed under it. [Assumption] that state is "the bag window is visible
/// but the game's inventory agent isn't active", so the X has nothing to close. When that lasts 3 s with no
/// retainer / shop window open and none of our runs going, it re-syncs the agent (show, then hide) and, if the
/// window is still there, closes it directly. Each step is logged (/xllog, "[Stash/Bags]") so a miss can be told
/// apart. /stash closebags does the same on demand, whatever the agent says.
/// </summary>
public sealed unsafe class BagWatchdog : IDisposable
{
    private static readonly string[] Bags = { "InventoryExpansion", "InventoryLarge", "Inventory" };
    private static readonly string[] Busy =
    {
        "RetainerList", "RetainerSellList", "RetainerSell", "InventoryRetainer", "InventoryRetainerLarge", "SelectString",
        "Shop", "ShopExchangeItem", "ShopExchangeCurrency", "InventoryBuddy", "InventoryBuddy2", "GrandCompanySupplyList",
        "ContextMenu", "Talk", "Request", "ItemSearch",
    };

    private readonly Plugin plugin;
    private DateTime next, stuckSince = DateTime.MinValue;
    private int step;
    private bool forced, logged;
    private DateTime lastOurRun = DateTime.MinValue;

    public BagWatchdog(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    /// <summary>Close a stuck bag window now (command).</summary>
    public void CloseNow()
    {
        if (OpenBags() == null) { Plugin.ChatGui.Print("[Stash] Your inventory window isn't open."); return; }
        forced = true;
        step = 1;
        next = DateTime.UtcNow;
    }

    private static AtkUnitBase* OpenBags()
    {
        foreach (var n in Bags) if (GameUi.Get(n) is var a && a != null) return a;
        return null;
    }

    private static AgentInterface* Agent() => AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory);

    private bool OurRunGoing =>
        plugin.Runner.Busy || plugin.Scrips.Busy || plugin.TurnIns.Busy || plugin.AfterAr.Busy;

    private static void Log(string msg) => Plugin.Log.Info($"[Stash/Bags] {msg}");

    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < next) return;
        next = DateTime.UtcNow.AddSeconds(0.5);
        try
        {
            var bags = OpenBags();
            if (bags == null) { if (step > 0) Log("bag window closed."); step = 0; forced = false; stuckSince = DateTime.MinValue; return; }

            if (step == 0)
            {
                if (OurRunGoing) { lastOurRun = DateTime.UtcNow; stuckSince = DateTime.MinValue; return; }
                var agent = Agent();
                var stuck = agent != null && !agent->IsAgentActive() && !Busy.Any(GameUi.IsOpen);
                if (!stuck) { stuckSince = DateTime.MinValue; logged = false; return; }
                if (stuckSince == DateTime.MinValue) { stuckSince = DateTime.UtcNow; return; }
                if ((DateTime.UtcNow - stuckSince).TotalSeconds < 3) return;
                // Only acts on its own right after one of our runs (so a guess wrong about the game's state can't
                // close a bag window you opened); otherwise it just notes it once.
                if ((DateTime.UtcNow - lastOurRun).TotalMinutes > 2)
                {
                    if (!logged) { logged = true; Log($"{bags->NameString} open with the inventory agent inactive (not after a run of ours: left alone; /stash closebags closes it)."); }
                    return;
                }
                Log($"{bags->NameString} is open but the inventory agent isn't, after a run: closing it.");
                step = 1;
            }

            var a = Agent();
            switch (step)
            {
                case 1: // re-sync: let the agent own the window again...
                    if (a != null) a->Show();
                    step = 2;
                    return;
                case 2: // ...then close it the normal way.
                    if (a != null) a->Hide();
                    step = 3;
                    next = DateTime.UtcNow.AddSeconds(1);
                    return;
                case 3: // still there: close the window itself.
                    Log($"{bags->NameString} still open after the agent hid: closing the window directly.");
                    bags->Close(true);
                    step = 4;
                    next = DateTime.UtcNow.AddSeconds(1);
                    return;
                default: // gave up for this time; logged so it can be looked at
                    if (step == 4) Log($"{bags->NameString} is still open after both tries (agent active: {(a != null && a->IsAgentActive())}).");
                    if (forced && step == 4) Plugin.ChatGui.PrintError("[Stash] Couldn't close the inventory window (logged in /xllog).");
                    step = 5;
                    return;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[Stash/Bags] failed");
            step = 5;
        }
    }
}
