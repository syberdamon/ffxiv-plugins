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
    private bool travelAsked, toldToClick, dumpedOther, clickedIndoor, bagsReset;
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
        skip.Clear(); refused.Clear(); stored = 0; retainerResets = 0; dumpedOther = false; clickedIndoor = false; bagsReset = false; housingBefore = new(); askedAt = DateTime.MinValue; travelAsked = false; toldToClick = false;
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
            if ((DateTime.UtcNow - phaseStart).TotalSeconds > limit)
            {
                DumpWindows($"timed out in {phase}");
                Fail($"Timed out ({phase}); dump saved.");
                return;
            }
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
            if (GameUi.Get(n) is var w && w != null) { w->Close(true); next = DateTime.UtcNow.AddSeconds(0.7); return; }
        if (Plugin.Condition[ConditionFlag.OccupiedSummoningBell]) return; // closing animation
        // The bag window can stay in "Select an item to entrust to your retainer" mode after the bell
        // closes, and its item menu then offers Entrust instead of Store: close it so it reopens clean.
        if (!bagsReset)
        {
            bagsReset = true;
            var closedAny = false;
            foreach (var n in new[] { "InventoryLarge", "Inventory", "InventoryExpansion" })
                if (GameUi.Get(n) is var inv && inv != null) { inv->Close(true); closedAny = true; }
            if (closedAny) { next = DateTime.UtcNow.AddSeconds(0.8); return; }
        }
        housingBefore = HousingWindows().ToHashSet();
        Plugin.Log.Info($"[Stash] storeroom: housing windows before: {string.Join(", ", housingBefore)}");
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

    private HashSet<string> housingBefore = new();
    private static readonly string[] MenuWindows = { "HousingMenu", "AddonContextMenuTitle", "SelectString", "ContextMenu" };

    private static IEnumerable<string> HousingWindows() =>
        GameUi.VisibleAddons().Where(n => n.StartsWith("Housing", StringComparison.Ordinal) && !MenuWindows.Contains(n));

    /// <summary>Layout mode: the Housing agent is active and a housing window has appeared that wasn't up before we started.</summary>
    /// <summary>
    /// Layout mode, read from what's on screen: the bag window's "Select furnishing to place." prompt, or the
    /// Indoor Furnishings list (its "Item Bind Prompt" / "Display Preview" toggles). The Housing agent's
    /// active flag isn't used: it doesn't track layout mode reliably.
    /// </summary>
    private static string? LayoutWindow()
    {
        foreach (var n in GameUi.VisibleAddons())
        {
            var a = GameUi.Get(n);
            if (a == null) continue;
            foreach (var t in GameUi.Texts(a))
            {
                var x = t.Trim();
                if (x.StartsWith("Select furnishing to place", StringComparison.OrdinalIgnoreCase) ||
                    x.Equals("Item Bind Prompt", StringComparison.OrdinalIgnoreCase) ||
                    x.Equals("Display Preview", StringComparison.OrdinalIgnoreCase) ||
                    x.Equals("Indoor Furnishings", StringComparison.OrdinalIgnoreCase)) return n; // the list's title (the menu is ruled out by the caller)
            }
        }
        return null;
    }

    private bool LayoutOpen => HousingMenu() == null && LayoutWindow() != null;

    /// <summary>The /housing menu, whatever its window is called: the one listing both "Indoor Furnishings" and "Move to Front Door".</summary>
    private static string? HousingMenu()
    {
        foreach (var n in GameUi.VisibleAddons())
        {
            var a = GameUi.Get(n);
            if (a == null) continue;
            var t = GameUi.Texts(a).Select(x => x.Trim()).ToList();
            if (t.Contains("Indoor Furnishings") && t.Contains("Move to Front Door")) return n;
        }
        return null;
    }

    private static bool AgentActive
    {
        get { var a = AgentModule.Instance()->GetAgentByInternalId(AgentId.Housing); return a != null && a->IsAgentActive(); }
    }

    /// <summary>Layout mode: a housing window is up, or we (or you) clicked Indoor Furnishings and the agent is active.</summary>
    private bool InLayout => LayoutOpen && !RetainerStillOpen;

    private void TickOpenMenu()
    {
        // Baseline once inside, before /housing: only windows that appear after this count as layout mode.
        if (askedAt == DateTime.MinValue && !clickedIndoor) housingBefore = HousingWindows().ToHashSet();
        if (InLayout) { Set(Phase.Store, "Storing furnishings..."); next = DateTime.UtcNow.AddSeconds(1); return; }
        // The housing menu: look for "Indoor Furnishings" in the menus we know how to click first,
        // then in every visible window (so an unexpected window gets dumped instead of silently missed).
        var known = new[] { "HousingMenu", "SelectString", "AddonContextMenuTitle", "ContextMenu" };
        if (askedAt != DateTime.MinValue && !dumpedOther)
            foreach (var other in GameUi.VisibleAddons().Except(known))
            {
                var o = GameUi.Get(other);
                if (o == null || !GameUi.Texts(o).Any(t => t.Trim().Equals("Indoor Furnishings", StringComparison.OrdinalIgnoreCase))) continue;
                dumpedOther = true;
                GameUi.Dump(other, "storeroom: 'Indoor Furnishings' is in this window");
                // [Assumption] a plain list menu: its callback takes the row number (Indoor Furnishings is row 0).
                var rows = GameUi.Texts(o).Select(x => x.Trim()).Where(x => x.Length > 0 && x != "Housing").ToList();
                var row = Math.Max(0, rows.IndexOf("Indoor Furnishings"));
                Plugin.Log.Info($"[Stash] storeroom: Indoor Furnishings found in {other}; clicking row {row}");
                o->FireCallbackInt(row);
                clickedIndoor = true;
                Set(Phase.Layout, "Opening Indoor Furnishings...");
                next = DateTime.UtcNow.AddSeconds(1.5);
                return;
            }
        foreach (var name in known)
        {
            var a = GameUi.Get(name);
            if (a == null) continue;
            for (var i = 0; i < a->AtkValuesCount; i++)
            {
                if (!GameUi.Str(a, i).Trim().Equals("Indoor Furnishings", StringComparison.OrdinalIgnoreCase)) continue;
                Plugin.Log.Info($"[Stash] storeroom: clicking Indoor Furnishings in {name} (value {i})");
                if (name == "SelectString") a->FireCallbackInt(GameUi.MenuEntries(a, false).FindIndex(r => r.Equals("Indoor Furnishings", StringComparison.OrdinalIgnoreCase)));
                else if (name == "ContextMenu") GameUi.Fire(a, 0, i - 8, 0);
                else a->FireCallbackInt(i - 7); // [Assumption] list rows from value 7, as the other menus
                clickedIndoor = true;
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
                DumpWindows("housing menu: Indoor Furnishings not found to click");
                Plugin.ChatGui.Print("[Stash] Click \"Indoor Furnishings\" in the housing menu and I'll carry on (I couldn't find it to click; a dump was saved).");
                Set(Phase.Layout, "Waiting for you to open Indoor Furnishings...");
            }
        }
    }

    private void TickLayout()
    {
        if (InLayout)
        {
            Plugin.Log.Info($"[Stash] storeroom: layout open; windows: {string.Join(", ", GameUi.VisibleAddons().OrderBy(x => x))}");
            Set(Phase.Store, "Storing furnishings...");
            next = DateTime.UtcNow.AddSeconds(1);
        }
    }

    private void TickStore()
    {
        if (!InLayout) { Fail("Indoor Furnishings closed."); return; }
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
                    bagsReset = false;
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
            if (AddonId() == 0)
            {
                AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory)->Show();
                next = DateTime.UtcNow.AddSeconds(0.8);
                return;
            }
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
        if (LayoutOpen && ++tries <= 6)
        {
            // Close the Indoor Furnishings list (that ends layout mode); the agent hide as a fallback.
            foreach (var n in GameUi.VisibleAddons().Where(n => n.StartsWith("Housing", StringComparison.Ordinal)))
                if (GameUi.Get(n) is var w && w != null) w->Close(true);
            if (tries > 3) AgentModule.Instance()->GetAgentByInternalId(AgentId.Housing)->Hide();
            next = DateTime.UtcNow.AddSeconds(0.8);
            return;
        }
        phase = Phase.Idle;
        Status = $"Stored {stored} furnishing(s) in the storeroom." + (refused.Count > 0 ? $" Not stored: {string.Join("; ", refused.Take(6))}." : "");
        Plugin.ChatGui.Print($"[Stash] {Status}");
    }

    /// <summary>A dump file listing every visible window plus the housing agent's state.</summary>
    private static void DumpWindows(string why)
    {
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Housing);
        var names = string.Join(", ", GameUi.VisibleAddons().OrderBy(x => x));
        GameUi.Dump("Windows", $"storeroom: {why}. Housing agent active: {agent != null && agent->IsAgentActive()}. Inside: {AtHome}. Visible windows: {names}");
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
