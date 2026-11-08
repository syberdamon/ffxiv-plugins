using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MaterialStash.Core;

namespace MaterialStash;

/// <summary>
/// Indoor furnishings from your bags into your house's storeroom: Lifestream home ("/li home"; set
/// Lifestream to enter the house), "/housing", "Indoor Furnishings" (layout mode), then each item's own
/// menu "Store" (the game offers it for furnishings while Indoor Furnishings is open). Renovation items
/// (walls, flooring, lights, exterior parts) and outdoor furnishings don't go in the indoor storeroom and
/// are never picked. Items the game won't store (storeroom full, not storable) are skipped and listed.
/// </summary>
public sealed unsafe class Storeroom : IDisposable
{
    private enum Phase { Idle, Prepare, Travel, OpenMenu, Layout, Store, Leave }

    private static readonly string[] NotIndoor =
    {
        "Ceiling", "Flooring", "Interior Wall", "Exterior", "Roof", "Door", "Window", "Fence", "Placard", "Outdoor",
        "Seed", "Soil", "Fertilizer", "Gardening", "Paint", "Interior Fixture",
    };

    private readonly Configuration config;
    private readonly LifestreamIpc lifestream = new();
    private Phase phase = Phase.Idle;
    private DateTime next = DateTime.MinValue, phaseStart = DateTime.MinValue, askedAt = DateTime.MinValue;
    private int stored, tries, retainerResets;
    private bool travelAsked, toldToClick;
    private (InventoryType Type, int Slot, uint Item)? current;
    private readonly HashSet<uint> skip = new();
    private readonly List<string> refused = new();
    private HashSet<uint>? indoorCats;

    public string Status { get; private set; } = "";
    public bool Busy => phase != Phase.Idle;

    public Storeroom(Configuration config)
    {
        this.config = config;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    /// <summary>[Assumption] Indoor furnishing categories: the Housing group less renovation, outdoor and gardening ones (by name).</summary>
    public HashSet<uint> IndoorCategories()
    {
        if (indoorCats != null) return indoorCats;
        var housing = config.Groups.FirstOrDefault(g => g.Name == "Housing")?.Categories ?? new HashSet<uint>();
        var sheet = Plugin.DataManager.GetExcelSheet<ItemUICategory>();
        indoorCats = housing.Where(id => sheet.GetRowOrDefault(id) is { } c &&
                                         !NotIndoor.Any(n => c.Name.ExtractText().Contains(n, StringComparison.OrdinalIgnoreCase)))
                            .ToHashSet();
        return indoorCats;
    }

    public bool IsIndoorFurnishing(uint itemId) => Items.Info(itemId) is { } i && IndoorCategories().Contains(i.CategoryId);

    public List<Tracker.Stack> InBags() =>
        Tracker.Stacks(Tracker.Bags, id => !skip.Contains(id) && IsIndoorFurnishing(id));

    public static bool AtHome
    {
        get
        {
            var hm = HousingManager.Instance();
            return hm != null && hm->IsInside();
        }
    }

    public bool LifestreamReady => lifestream.IsLoaded;

    public void Start()
    {
        if (Busy) return;
        skip.Clear(); refused.Clear(); stored = 0; retainerResets = 0; travelAsked = false; toldToClick = false;
        if (InBags().Count == 0) { Status = "No indoor furnishings in your bags (\"Furnishings out\" at the bell brings them from your retainers)."; return; }
        if (!AtHome && !lifestream.IsLoaded) { Status = "Go inside your house first (or install Lifestream so I can take you)."; return; }
        // Close the bell / retainer first: with a retainer open the item menu offers "Entrust to Retainer", not "Store".
        Set(Phase.Prepare, "Closing the retainer...");
    }

    public void Stop()
    {
        if (!Busy) return;
        phase = Phase.Idle;
        Status = $"Stopped ({stored} stored).";
    }

    private void Set(Phase p, string status) { phase = p; phaseStart = DateTime.UtcNow; tries = 0; current = null; Status = status; }

    private void Fail(string why)
    {
        phase = Phase.Idle;
        Status = $"{why} ({stored} stored)";
        Plugin.ChatGui.PrintError($"[Stash] {Status}");
    }

    private void OnUpdate(IFramework framework)
    {
        if (phase == Phase.Idle || DateTime.UtcNow < next) return;
        next = DateTime.UtcNow.AddSeconds(0.5);
        try
        {
            var limit = phase switch { Phase.Travel => 240, Phase.Layout => 90, _ => 30 };
            if ((DateTime.UtcNow - phaseStart).TotalSeconds > limit) { Fail($"Timed out ({phase})."); return; }
            switch (phase)
            {
                case Phase.Prepare: TickPrepare(); break;
                case Phase.Travel: TickTravel(); break;
                case Phase.OpenMenu: TickOpenMenu(); break;
                case Phase.Layout: TickLayout(); break;
                case Phase.Store: TickStore(); break;
                case Phase.Leave: TickLeave(); break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Material Stash: storeroom run failed");
            Fail("Error (see /xllog).");
        }
    }

    private static readonly string[] RetainerWindows =
        { "InventoryRetainerLarge", "InventoryRetainer", "RetainerGrid0", "RetainerSellList", "RetainerSell", "SelectString", "RetainerList", "Bank" };

    private static bool RetainerStillOpen =>
        Plugin.Condition[ConditionFlag.OccupiedSummoningBell] || RetainerWindows.Any(GameUi.IsOpen);

    /// <summary>Close any retainer/bell windows (one per tick, innermost first), then go home or straight to the menu.</summary>
    private void TickPrepare()
    {
        if (GameUi.Get("Talk") is var talk && talk != null) { GameUi.ClickTalk(talk); return; }
        foreach (var n in RetainerWindows)
            if (GameUi.IsOpen(n)) { GameUi.Close(n); next = DateTime.UtcNow.AddSeconds(0.7); return; }
        if (Plugin.Condition[ConditionFlag.OccupiedSummoningBell]) return; // closing animation
        if (AtHome) { Set(Phase.OpenMenu, "Opening the housing menu..."); next = DateTime.UtcNow.AddSeconds(1); }
        else Set(Phase.Travel, "Going home (Lifestream)...");
    }

    private void TickTravel()
    {
        if (GameUi.IsOpen("RetainerList")) { GameUi.Close("RetainerList"); return; }
        if (GameUi.Get("Talk") is var talk && talk != null) { GameUi.ClickTalk(talk); return; }
        if (!travelAsked)
        {
            if (Plugin.Condition[ConditionFlag.OccupiedSummoningBell] || Plugin.Condition[ConditionFlag.InCombat]) return;
            if (!lifestream.Execute("home")) { Fail("Lifestream didn't take the request."); return; }
            travelAsked = true;
            next = DateTime.UtcNow.AddSeconds(2);
            return;
        }
        if (lifestream.IsBusy || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.ObjectTable.LocalPlayer == null) return;
        if (AtHome) { Set(Phase.OpenMenu, "Opening the housing menu..."); next = DateTime.UtcNow.AddSeconds(2); return; }
        if ((DateTime.UtcNow - phaseStart).TotalSeconds > 20)
            Fail("Lifestream took you home but not inside. In Lifestream's house settings, set the teleport behaviour to \"Enter house\", or walk in and press again.");
    }

    private static bool LayoutOpen
    {
        get
        {
            // [Assumption] The Housing agent alone can read active while just standing inside, so also
            // require one of its windows (the furnishings list or the layout bar) to be showing.
            var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Housing);
            if (agent == null || !agent->IsAgentActive()) return false;
            return GameUi.VisibleAddons().Any(n => n.StartsWith("Housing", StringComparison.Ordinal));
        }
    }

    private void TickOpenMenu()
    {
        if (LayoutOpen) { Set(Phase.Store, "Storing furnishings..."); next = DateTime.UtcNow.AddSeconds(1); return; }
        // The housing menu: find its "Indoor Furnishings" entry wherever it lists it.
        foreach (var name in new[] { "HousingMenu", "SelectString", "AddonContextMenuTitle", "ContextMenu" })
        {
            var a = GameUi.Get(name);
            if (a == null) continue;
            for (var i = 0; i < a->AtkValuesCount; i++)
            {
                if (!GameUi.Str(a, i).Equals("Indoor Furnishings", StringComparison.OrdinalIgnoreCase)) continue;
                if (name == "SelectString") a->FireCallbackInt(GameUi.MenuEntries(a, false).FindIndex(r => r.Equals("Indoor Furnishings", StringComparison.OrdinalIgnoreCase)));
                else if (name == "ContextMenu") GameUi.Fire(a, 0, i - 8, 0);
                else a->FireCallbackInt(i - 7); // [Assumption] list rows from value 7, as the other menus
                Set(Phase.Layout, "Opening Indoor Furnishings...");
                next = DateTime.UtcNow.AddSeconds(1.5);
                return;
            }
            if (!toldToClick) GameUi.Dump(name, "storeroom: housing menu (looking for Indoor Furnishings). Visible windows: " + string.Join(", ", GameUi.VisibleAddons().OrderBy(x => x)));
        }
        if (askedAt == DateTime.MinValue || (DateTime.UtcNow - askedAt).TotalSeconds > 6)
        {
            SendChat("/housing");
            askedAt = DateTime.UtcNow;
            next = DateTime.UtcNow.AddSeconds(1.5);
            if (++tries > 2 && !toldToClick)
            {
                toldToClick = true;
                Plugin.ChatGui.Print("[Stash] Click \"Indoor Furnishings\" in the housing menu and I'll carry on (I couldn't find it to click; a dump was saved).");
                Set(Phase.Layout, "Waiting for you to open Indoor Furnishings...");
            }
        }
    }

    private void TickLayout()
    {
        if (LayoutOpen) { Set(Phase.Store, "Storing furnishings..."); next = DateTime.UtcNow.AddSeconds(1); }
    }

    private void TickStore()
    {
        if (!LayoutOpen) { Fail("Indoor Furnishings closed."); return; }
        if (RetainerStillOpen) { GameUi.Close("ContextMenu"); current = null; Set(Phase.Prepare, "Closing the retainer..."); return; }
        var im = InventoryManager.Instance();
        var yn = GameUi.Get("SelectYesno");
        if (yn != null && current != null) { yn->FireCallbackInt(0); next = DateTime.UtcNow.AddSeconds(1); return; }

        if (current is { } c)
        {
            var slot = im->GetInventorySlot(c.Type, c.Slot);
            if (slot == null || slot->ItemId != c.Item) { stored++; current = null; GameUi.Close("ContextMenu"); return; }
            var menu = GameUi.Get("ContextMenu");
            if (menu != null)
            {
                var index = -1;
                var entries = new List<string>();
                for (var i = 7; i < menu->AtkValuesCount; i++)
                {
                    var t = GameUi.Str(menu, i);
                    if (t.Length == 0) continue;
                    entries.Add(t);
                    if (index < 0 && t.Contains("Store", StringComparison.OrdinalIgnoreCase) && !t.Contains("Restore", StringComparison.OrdinalIgnoreCase)) index = i - 8;
                }
                if (index < 0 && entries.Any(e => e.Contains("Entrust to Retainer", StringComparison.OrdinalIgnoreCase)))
                {
                    // A retainer is still attached to the item menu: close it and try this item again.
                    menu->FireCallbackInt(-1);
                    current = null;
                    if (++retainerResets > 3) { Fail("A retainer window keeps the item menu from offering Store. Close the retainer and run /stash storeroom."); return; }
                    Set(Phase.Prepare, "Closing the retainer...");
                    return;
                }
                if (index < 0)
                {
                    if (refused.Count == 0) GameUi.Dump("ContextMenu", "storeroom: no Store entry. Visible windows: " + string.Join(", ", GameUi.VisibleAddons().OrderBy(x => x)));
                    menu->FireCallbackInt(-1);
                    Skip(c, slot, $"no Store option ({string.Join(" | ", entries.Take(6))})");
                    return;
                }
                GameUi.Fire(menu, 0, index, 0);
                next = DateTime.UtcNow.AddSeconds(1.2);
                tries++;
                return;
            }
            if (++tries > 6) { Skip(c, slot, "not taken (storeroom full?)"); return; }
            AgentInventoryContext.Instance()->OpenForItemSlot(c.Type, c.Slot, 0, AddonId());
            next = DateTime.UtcNow.AddSeconds(0.8);
            return;
        }

        var nextStack = InBags().FirstOrDefault();
        if (nextStack.ItemId == 0) { Set(Phase.Leave, "Closing..."); return; }
        current = (nextStack.Type, nextStack.Slot, nextStack.ItemId % 1_000_000);
        tries = 0;
    }

    private void Skip((InventoryType Type, int Slot, uint Item) c, InventoryItem* slot, string why)
    {
        var key = c.Item + ((slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0 ? 1_000_000u : 0u);
        skip.Add(key);
        refused.Add($"{Items.Info(c.Item)?.Name}: {why}");
        current = null;
        // Several in a row with no Store option: the furnishing mode isn't the right one; stop rather than loop.
        if (refused.Count >= 5 && stored == 0) Fail("The game isn't offering \"Store\" for these (dump saved).");
    }

    private void TickLeave()
    {
        if (LayoutOpen)
        {
            AgentModule.Instance()->GetAgentByInternalId(AgentId.Housing)->Hide();
            return;
        }
        phase = Phase.Idle;
        Status = $"Stored {stored} furnishing(s) in the storeroom." + (refused.Count > 0 ? $" Not stored: {string.Join("; ", refused.Take(6))}." : "");
        Plugin.ChatGui.Print($"[Stash] {Status}");
    }

    private static uint AddonId()
    {
        foreach (var n in new[] { "InventoryLarge", "Inventory", "InventoryExpansion" })
        {
            var a = GameUi.Get(n);
            if (a != null) return a->Id;
        }
        return 0;
    }

    private static void SendChat(string line)
    {
        var ui = UIModule.Instance();
        if (ui == null) return;
        var str = Utf8String.FromString(line);
        try { ui->ProcessChatBoxEntry(str); }
        finally { str->Dtor(true); }
    }
}
