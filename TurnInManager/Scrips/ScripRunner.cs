using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Syberdamon.Shared;
using TurnInManager.Core;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace TurnInManager.Scrips;

public enum ScripPhase { Idle, Teleport, Aethernet, Walking, Talking, TurningIn, Buying, Done }

/// <summary>
/// One scrip run for one item you want from the scrip exchange:
///  1. Travel to your scrip hub (Lifestream teleport + aethernet) unless the NPCs are already in sight.
///  2. Collectable Appraiser: turn in every collectable in your bags that pays the item's scrip.
///  3. Before a turn-in would push scrips over the cap (4,000): walk to the Scrip Exchange and buy the
///     item (up to the number asked for), then come back and keep turning in.
///  4. At the end, buy as many as the scrips allow (up to the number asked for).
/// Window callbacks follow GatherBuddy Reborn / TheCollector (see THIRD_PARTY_NOTICES). Every
/// turn-in is checked against the scrips you hold and every purchase against your bags; anything
/// unexpected stops the run.
/// </summary>
public sealed unsafe class ScripRunner : IDisposable
{
    private readonly Configuration config;
    private readonly VNavIpc vnav;
    private readonly LifestreamIpc lifestream;

    public ScripPhase Phase { get; private set; } = ScripPhase.Idle;
    public string Status { get; private set; } = "";
    public bool Busy => Phase is not (ScripPhase.Idle or ScripPhase.Done);
    public int TurnedIn { get; private set; }
    public int ScripsEarned { get; private set; }
    public int Bought { get; private set; }
    /// <summary>The last run ended with an error (Status says why).</summary>
    public bool Failed { get; private set; }

    private uint target;
    private ScripShopEntry entry = null!;
    private int buyLeft;
    private string npcName = "";
    private ScripPhase afterTalk;
    private DateTime phaseStarted, nextAction, talkedAt, lastProgress;
    private float bestDistance;
    private int tries;
    private bool aethernetTried;

    // turn-in
    private int jobTab = -1;
    private string selected = "";
    private (int Scrips, DateTime At)? submitted;

    // buying
    private int searchPage = -1, searchSub = 0, searchStage, shownSub = -1;
    private (int Count, int Scrips, int Qty, DateTime At)? purchase;
    private bool menuPicked;
    private string lastMenu = "";

    private static IDataManager Data => Plugin.DataManager;

    public ScripRunner(Configuration config, VNavIpc vnav, LifestreamIpc lifestream)
    {
        this.config = config;
        this.vnav = vnav;
        this.lifestream = lifestream;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        if (Busy) vnav.Stop();
    }

    // ================================================================ start / stop

    public ScripShopEntry? Entry(uint itemId) => ScripData.Shop(Data).TryGetValue(itemId % 1_000_000, out var e) ? e : null;

    /// <summary>Start a run for this item. Returns "" when started, otherwise why not.</summary>
    public string Start(uint itemId, int maxBuy)
    {
        if (Plugin.ObjectTable.LocalPlayer == null) return Refuse("Not logged in.");
        if (Busy) return Refuse("A scrip run is already going.");
        if (Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.BoundByDuty]) return Refuse("In combat or a duty.");
        if (!vnav.IsLoaded) return Refuse("vnavmesh isn't installed or loaded.");
        if (Entry(itemId) is not { } e) return Refuse($"Item #{itemId} isn't sold for scrips.");
        entry = e;
        target = e.ItemId;
        buyLeft = Math.Max(0, maxBuy);
        TurnedIn = ScripsEarned = Bought = 0;
        Failed = false;
        aethernetTried = false;
        lock (RunLog) RunLog.Clear();
        var coll = Payable();
        var scrips = ScripData.Scrips(e.CurrencyItemId);
        Log($"Start: {e.Name} x{buyLeft} ({e.Cost} {ScripName} each); {scrips} scrips, {coll.Count} collectable(s) to turn in");
        if (coll.Count == 0 && (buyLeft == 0 || scrips < e.Cost))
            return Refuse($"No collectables that pay {ScripName} in your bags, and {(buyLeft == 0 ? "nothing to buy" : $"{scrips} scrips won't buy one ({e.Cost})")}.");
        NextStep();
        return Busy ? "" : Status;
    }

    private string Refuse(string why) { Status = why; return why; }

    public void Stop(string why = "Stopped.")
    {
        vnav.Stop();
        if (lifestream.IsBusy) lifestream.Abort();
        Log("STOP: " + why);
        Set(ScripPhase.Idle, why);
    }

    private string ScripName => ScripData.ScripNames.GetValueOrDefault(entry.CurrencyItemId, "scrips");

    /// <summary>Collectables in your bags that pay this run's scrip and meet the lowest tier, in bag order.</summary>
    private List<(InventoryType Type, int Slot, uint ItemId, int Collectability)> Payable()
    {
        var rw = ScripData.Rewards(Data);
        return ScripData.CollectablesInBags(Data, entry.CurrencyItemId)
            .Where(c => rw.TryGetValue(c.ItemId, out var r) && ScripData.Pays(r, c.Collectability) > 0)
            .ToList();
    }

    private bool CanBuy(int scrips) => buyLeft > 0 && scrips >= entry.Cost && (FreeSlots() > 0 || ItemCount(target) % StackSize(target) != 0);

    /// <summary>Where to next: the appraiser while there are collectables (buying first if the cap is near), then the shop.</summary>
    private void NextStep()
    {
        var scrips = ScripData.Scrips(entry.CurrencyItemId);
        var coll = Payable();
        if (coll.Count > 0)
        {
            var r = ScripData.Rewards(Data)[coll[0].ItemId];
            var pay = ScripData.Pays(r, coll[0].Collectability);
            if (scrips + pay > ScripData.ScripCap)
            {
                if (CanBuy(scrips)) { GoTo(config.ExchangeName, ScripPhase.Buying); return; }
                Finish($"{ScripName} would go over {ScripData.ScripCap:N0} and there's nothing more to buy: {coll.Count} collectable(s) left in your bags.");
                return;
            }
            GoTo(config.AppraiserName, ScripPhase.TurningIn);
            return;
        }
        if (CanBuy(scrips)) { GoTo(config.ExchangeName, ScripPhase.Buying); return; }
        Finish();
    }

    // ================================================================ tick

    private void OnUpdate(IFramework framework)
    {
        if (!Busy) return;
        try
        {
            if (Plugin.Condition[ConditionFlag.InCombat]) { Fail("Combat started."); return; }
            if ((DateTime.UtcNow - phaseStarted).TotalSeconds > Timeout(Phase)) { Fail($"Timed out: {Status}"); return; }
            if (DateTime.UtcNow < nextAction) return;
            switch (Phase)
            {
                case ScripPhase.Teleport: TickTeleport(); break;
                case ScripPhase.Aethernet: TickAethernet(); break;
                case ScripPhase.Walking: TickWalk(); break;
                case ScripPhase.Talking: TickTalk(); break;
                case ScripPhase.TurningIn: TickTurnIn(); break;
                case ScripPhase.Buying: TickBuy(); break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Scrip run failed");
            Fail("Something went wrong (see /xllog).");
        }
    }

    private static double Timeout(ScripPhase p) => p switch
    {
        ScripPhase.Teleport => 60,
        ScripPhase.Aethernet => 40,
        ScripPhase.Walking => 120,
        ScripPhase.Buying => 90, // reset on every purchase / page change
        _ => 30,
    };

    // ================================================================ travel

    private void GoTo(string npc, ScripPhase then)
    {
        npcName = npc;
        afterTalk = then;
        tries = 0;
        talkedAt = DateTime.MinValue;
        menuPicked = false;
        CloseShopWindows();
        if (FindNpc() != null) { StartWalk(); return; }
        if (Plugin.ClientState.TerritoryType == config.HubTerritory) { HopAethernetOrWalk(); return; }
        if (!config.UseLifestream || !lifestream.IsLoaded)
        {
            Fail($"Go to your scrip hub first (or install Lifestream so I can take you): can't see the {npc}.");
            return;
        }
        Log($"Teleport: aetheryte {config.HubAetheryte}");
        if (!lifestream.Teleport(config.HubAetheryte, 0)) { Fail("Lifestream refused the teleport (unlocked? enough gil?)."); return; }
        Set(ScripPhase.Teleport, "Teleporting to the scrip hub...");
        nextAction = DateTime.UtcNow.AddSeconds(2);
    }

    private static bool Moving => Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51] || Plugin.Condition[ConditionFlag.Casting];

    private void TickTeleport()
    {
        if (lifestream.IsBusy || Moving) return;
        if (Plugin.ClientState.TerritoryType != config.HubTerritory)
        {
            if ((DateTime.UtcNow - phaseStarted).TotalSeconds > 25) Fail("The teleport didn't reach the scrip hub.");
            return;
        }
        if (Plugin.ObjectTable.LocalPlayer == null) return;
        HopAethernetOrWalk();
    }

    private void HopAethernetOrWalk()
    {
        if (FindNpc() != null) { StartWalk(); return; }
        if (config.HubAethernet != 0 && !aethernetTried && lifestream.IsLoaded)
        {
            aethernetTried = true;
            Log($"Aethernet: {config.HubAethernet}");
            if (lifestream.AethernetTeleportById(config.HubAethernet))
            {
                Set(ScripPhase.Aethernet, "Taking the aethernet...");
                nextAction = DateTime.UtcNow.AddSeconds(2);
                return;
            }
            Log("  Lifestream refused the aethernet hop (not near the aetheryte?)");
        }
        if (NpcLocator.InTerritory(npcName, Plugin.ClientState.TerritoryType) != null) { StartWalk(); return; }
        Fail($"Can't find the {npcName} here. Stand near it (or check the NPC names in the Scrips tab) and run again.");
    }

    private void TickAethernet()
    {
        if (lifestream.IsBusy || Moving) return;
        if (Plugin.ObjectTable.LocalPlayer == null) return;
        if ((DateTime.UtcNow - phaseStarted).TotalSeconds < 3) return;
        HopAethernetOrWalk();
    }

    private IGameObject? FindNpc()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        var npcs = Plugin.ObjectTable.Where(o => o.ObjectKind == ObjectKind.EventNpc && o.IsTargetable).ToList();
        var exact = npcs.FirstOrDefault(o => o.Name.TextValue.Equals(npcName, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        // Fallback: a nearby NPC whose name has the key word (hubs name these NPCs slightly differently).
        var key = afterTalk == ScripPhase.TurningIn ? "Appraiser" : "Scrip";
        return me == null ? null : npcs.Where(o => o.Name.TextValue.Contains(key, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => Vector3.Distance(o.Position, me.Position)).FirstOrDefault();
    }

    private void StartWalk()
    {
        Set(ScripPhase.Walking, $"Walking to the {npcName}...");
        bestDistance = float.MaxValue;
        lastProgress = DateTime.UtcNow;
    }

    private void TickWalk()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null || !vnav.IsReady) return;
        if (GameUi.IsOpen("SelectString") || GameUi.IsOpen("SelectIconString") || GameUi.IsOpen("Talk")) { CloseMenus(); return; }
        var npc = FindNpc();
        if (npc != null && Vector3.Distance(me.Position, npc.Position) <= 4.5f) { vnav.Stop(); Set(ScripPhase.Talking, $"Talking to the {npc.Name.TextValue}..."); tries = 0; return; }
        Vector3 goal;
        if (npc != null) goal = npc.Position;
        else if (NpcLocator.InTerritory(npcName, Plugin.ClientState.TerritoryType) is { } spot) goal = spot;
        else { Fail($"Can't find the {npcName} here."); return; }
        var dest = vnav.NearestPoint(goal, 6, 6) ?? goal;
        var dist = Vector3.Distance(me.Position, dest);
        if (dist <= 1.5f && npc != null && Vector3.Distance(me.Position, npc.Position) <= 8) { vnav.Stop(); Set(ScripPhase.Talking, $"Talking to the {npc.Name.TextValue}..."); tries = 0; return; }
        if (dist < bestDistance - 0.5f) { bestDistance = dist; lastProgress = DateTime.UtcNow; }
        if ((DateTime.UtcNow - lastProgress).TotalSeconds > 8) { Fail($"vnavmesh isn't getting closer to the {npcName}."); return; }
        if (!vnav.IsBusy) vnav.MoveCloseTo(dest, 1f);
        nextAction = DateTime.UtcNow.AddSeconds(0.5);
    }

    private void TickTalk()
    {
        if (afterTalk == ScripPhase.TurningIn && Ready("CollectablesShop")) { BeginTurnIn(); return; }
        if (afterTalk == ScripPhase.Buying && Ready("InclusionShop")) { BeginBuying(); return; }

        // A menu after talking: pick the collectables / scrip exchange row.
        foreach (var (name, icon) in new[] { ("SelectIconString", true), ("SelectString", false) })
        {
            var menu = GameUi.Get(name);
            if (menu == null) continue;
            if (talkedAt == DateTime.MinValue) { CloseMenus(); return; }
            var rows = GameUi.MenuEntries(menu, icon);
            if (rows.Count == 0 || rows.All(r => r.Length == 0)) return; // still filling in
            var joined = string.Join("|", rows);
            if (menuPicked && joined == lastMenu) return; // the pick is on its way
            Log($"  {npcName} menu: {string.Join(" | ", rows)}");
            var row = PickRow(rows);
            if (row < 0) { Fail($"No matching row in the {npcName}'s menu: {string.Join(" | ", rows)}"); return; }
            menu->FireCallbackInt(row);
            menuPicked = true;
            lastMenu = joined;
            nextAction = DateTime.UtcNow.AddSeconds(1);
            return;
        }
        var talk = GameUi.Get("Talk");
        if (talk != null) { GameUi.ClickTalk(talk); nextAction = DateTime.UtcNow.AddSeconds(0.5); return; }
        if (menuPicked && (DateTime.UtcNow - talkedAt).TotalSeconds < 6) return; // window on its way

        if (++tries > 3) { Fail($"The {npcName} didn't open its window."); return; }
        var npc = FindNpc();
        if (npc == null) { Fail($"Lost sight of the {npcName}."); return; }
        menuPicked = false;
        lastMenu = "";
        TargetSystem.Instance()->InteractWithObject((GameObjectStruct*)npc.Address, true);
        talkedAt = DateTime.UtcNow;
        nextAction = DateTime.UtcNow.AddSeconds(1);
    }

    private int PickRow(List<string> rows)
    {
        string[] want = afterTalk == ScripPhase.TurningIn
            ? new[] { "collectable", "appraise", "trade" }
            : ScripData.IsCrafterScrip(entry.CurrencyItemId) ? new[] { "crafter", "scrip" } : new[] { "gatherer", "scrip" };
        foreach (var w in want)
        {
            var i = rows.FindIndex(r => r.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        // [Assumption] the first row opens the window when nothing matches (as TheCollector does).
        return rows[0].StartsWith("Nothing", StringComparison.OrdinalIgnoreCase) || rows[0].StartsWith("Cancel", StringComparison.OrdinalIgnoreCase) ? -1 : 0;
    }

    // ================================================================ turn in

    private void BeginTurnIn()
    {
        jobTab = -1;
        selected = "";
        submitted = null;
        Set(ScripPhase.TurningIn, "Turning in collectables...");
        nextAction = DateTime.UtcNow.AddSeconds(0.8);
    }

    private void TickTurnIn()
    {
        var addon = GameUi.Get("CollectablesShop");
        if (addon == null || !IsReady(addon)) { Fail("The collectables window closed."); return; }
        var scrips = ScripData.Scrips(entry.CurrencyItemId);

        if (submitted is { } s)
        {
            if (scrips > s.Scrips)
            {
                TurnedIn++;
                ScripsEarned += scrips - s.Scrips;
                Log($"  +{scrips - s.Scrips} {ScripName} (now {scrips})");
                submitted = null;
                phaseStarted = DateTime.UtcNow;
                Status = $"Turned in {TurnedIn} (+{ScripsEarned} {ScripName})...";
                nextAction = DateTime.UtcNow.AddSeconds(config.ScripStepDelay);
                return;
            }
            if ((DateTime.UtcNow - s.At).TotalSeconds > 8) { GameUi.Dump("CollectablesShop", "no scrips after a turn-in"); Fail($"Turned in '{selected}' but no scrips came: stopped."); }
            return;
        }

        var coll = Payable();
        if (coll.Count == 0) { addon->Close(true); NextStep(); return; }
        var c = coll[0];
        var r = ScripData.Rewards(Data)[c.ItemId];
        if (scrips + ScripData.Pays(r, c.Collectability) > ScripData.ScripCap) { addon->Close(true); NextStep(); return; }

        if (r.JobTab >= 0 && r.JobTab != jobTab)
        {
            Fire(addon, false, 14, (uint)r.JobTab);
            jobTab = r.JobTab;
            selected = "";
            nextAction = DateTime.UtcNow.AddSeconds(config.ScripStepDelay + 0.3);
            return;
        }
        if (!selected.Equals(r.Name, StringComparison.OrdinalIgnoreCase))
        {
            var index = TurnInIndex(addon, r.Name);
            if (index < 0)
            {
                if (++tries > 6) { GameUi.Dump("CollectablesShop", $"'{r.Name}' not in the list"); Fail($"'{r.Name}' isn't in the appraiser's list (job tab {r.JobTab})."); }
                nextAction = DateTime.UtcNow.AddSeconds(0.5);
                return;
            }
            tries = 0;
            Fire(addon, false, 12, (uint)index);
            selected = r.Name;
            nextAction = DateTime.UtcNow.AddSeconds(config.ScripStepDelay + 0.3);
            return;
        }
        Log($"  turning in {r.Name} (collectability {c.Collectability})");
        Fire(addon, true, 15, 0u);
        submitted = (scrips, DateTime.UtcNow);
        nextAction = DateTime.UtcNow.AddSeconds(config.ScripStepDelay);
    }

    /// <summary>
    /// The appraiser's item list is a tree list (node 28): headers, then one row per item you hold.
    /// The callback wants the row's position counting item rows only (headers skipped).
    /// </summary>
    private static int TurnInIndex(AtkUnitBase* addon, string name)
    {
        AtkComponentTreeList* tree = null;
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != (NodeType)1028 || node->NodeId != 28) continue;
            var comp = node->GetAsAtkComponentNode();
            if (comp == null || comp->Component == null) continue;
            tree = (AtkComponentTreeList*)comp->Component;
            break;
        }
        if (tree == null) return -1;
        var leaf = 0;
        for (var i = 0; i < (int)tree->Items.Count; i++)
        {
            var item = tree->Items[i].Value;
            if (item == null) continue;
            var raw = item->UIntValues.Count > 0 ? item->UIntValues[0] : 0u;
            var type = (AtkComponentTreeListItemType)(raw & 0xF);
            if (type is AtkComponentTreeListItemType.GroupHeader or AtkComponentTreeListItemType.CollapsibleGroupHeader) continue;
            var label = item->StringValues.Count > 0 ? SeString.Parse(item->StringValues[0].Value).TextValue : "";
            if (label.Contains(name, StringComparison.OrdinalIgnoreCase)) return leaf;
            leaf++;
        }
        return -1;
    }

    // ================================================================ buy

    private void BeginBuying()
    {
        purchase = null;
        searchStage = 0;
        shownSub = -1;
        if (config.ShopSpots.TryGetValue(target, out var spot)) { searchPage = spot[0]; searchSub = spot[1]; searchStage = 1; }
        else { searchPage = 0; searchSub = 1; searchStage = 2; }
        Set(ScripPhase.Buying, $"Buying {entry.Name}...");
        nextAction = DateTime.UtcNow.AddSeconds(0.8);
    }

    private void TickBuy()
    {
        // Purchase confirmations first.
        var dlg = GameUi.Get("ShopExchangeItemDialog");
        if (dlg != null) { Fire(dlg, false, 0); dlg->Close(true); nextAction = DateTime.UtcNow.AddSeconds(0.5); return; }
        var yn = GameUi.Get("SelectYesno");
        if (yn != null) { Log($"  yes/no: {GameUi.YesNoText()}"); yn->FireCallbackInt(0); nextAction = DateTime.UtcNow.AddSeconds(0.5); return; }

        var addon = GameUi.Get("InclusionShop");
        if (addon == null || !IsReady(addon)) { Fail("The scrip exchange window closed."); return; }
        var scrips = ScripData.Scrips(entry.CurrencyItemId);

        if (purchase is { } p)
        {
            var have = ItemCount(target);
            if (have >= p.Count + p.Qty && scrips <= p.Scrips - entry.Cost * p.Qty)
            {
                Bought += p.Qty;
                buyLeft -= p.Qty;
                Log($"  bought {p.Qty} {entry.Name} (scrips {p.Scrips} -> {scrips})");
                Status = $"Bought {Bought} {entry.Name}...";
                purchase = null;
                phaseStarted = DateTime.UtcNow;
                nextAction = DateTime.UtcNow.AddSeconds(config.ScripStepDelay);
                return;
            }
            if ((DateTime.UtcNow - p.At).TotalSeconds > 8) { GameUi.Dump("InclusionShop", "purchase not seen"); Fail($"Asked for {p.Qty} {entry.Name} but they didn't arrive: stopped."); }
            return;
        }

        if (!CanBuy(scrips))
        {
            addon->Close(true);
            if (Payable().Count > 0) { NextStep(); return; }
            Finish();
            return;
        }

        // Find the item: a remembered page/sub-page first, else page by page.
        var index = ShopIndex(addon, target);
        if (index < 0)
        {
            if (!NextShopPage(addon)) return;
            nextAction = DateTime.UtcNow.AddSeconds(config.ScripStepDelay + 0.2);
            return;
        }
        // Remember where it was (only once we've chosen a sub-page ourselves, so the spot is known).
        if (searchStage != 0 && searchPage >= 0 && shownSub > 0) { config.ShopSpots[target] = new[] { searchPage, shownSub }; config.Save(); }
        searchStage = 0;

        var stack = StackSize(target);
        var room = FreeSlots() * stack + (stack - ItemCount(target) % stack) % stack;
        var qty = Math.Min(Math.Min(buyLeft, scrips / entry.Cost), Math.Min(99, room));
        if (qty <= 0) { Fail("No room in your bags for the purchase."); return; }
        Log($"  buying {qty} {entry.Name} (row {index})");
        purchase = (ItemCount(target), scrips, qty, DateTime.UtcNow);
        Fire(addon, false, 14, (uint)index, (uint)qty);
        nextAction = DateTime.UtcNow.AddSeconds(0.6);
    }

    /// <summary>Move the shop to the next page/sub-page to look at. False (after failing the run) when every page was searched.</summary>
    private bool NextShopPage(AtkUnitBase* addon)
    {
        var pages = DropdownLength(addon, 7);
        var subs = DropdownLength(addon, 9);
        switch (searchStage)
        {
            case 1: // remembered spot: show it
                SelectPage(addon, searchPage);
                searchStage = 3;
                return true;
            case 3: // remembered page shown: its sub-page
                SelectSubPage(addon, searchSub);
                searchStage = 4;
                return true;
            case 4: // not where it was: search from the start
                config.ShopSpots.Remove(target);
                searchPage = 0; searchSub = 1; searchStage = 2;
                SelectPage(addon, 0);
                searchStage = 5;
                return true;
            case 2:
                SelectPage(addon, searchPage);
                searchStage = 5;
                return true;
            case 5: // page shown: walk its sub-pages
                if (subs > 0 && searchSub <= subs) { SelectSubPage(addon, searchSub); searchSub++; return true; }
                searchPage++;
                searchSub = 1;
                if (pages > 0 && searchPage >= pages) { GameUi.Dump("InclusionShop", $"{entry.Name} not found"); Fail($"Couldn't find {entry.Name} in the scrip exchange."); return false; }
                SelectPage(addon, searchPage);
                return true;
        }
        Fail("Lost track of the shop pages.");
        return false;
    }

    /// <summary>The visible row selling this item: value 298 holds the row count, rows are 18 values apart from 300.</summary>
    private static int ShopIndex(AtkUnitBase* addon, uint itemId)
    {
        var n = (int)GameUi.UInt(addon, 298);
        for (var i = 0; i < n && i < 200; i++)
            if (GameUi.UInt(addon, 300 + i * 18) == itemId) return i;
        return -1;
    }

    private static AtkComponentDropDownList* Dropdown(AtkUnitBase* addon, uint nodeId)
    {
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != (NodeType)1015 || node->NodeId != nodeId) continue;
            var comp = node->GetAsAtkComponentNode();
            if (comp == null || comp->Component == null) return null;
            return node->GetAsAtkComponentDropdownList();
        }
        return null;
    }

    private static int DropdownLength(AtkUnitBase* addon, uint nodeId)
    {
        var d = Dropdown(addon, nodeId);
        return d == null || d->List == null ? 0 : d->List->ListLength;
    }

    private void SelectPage(AtkUnitBase* addon, int page)
    {
        var d = Dropdown(addon, 7);
        if (d != null) d->SelectItem(page);
        Fire(addon, false, 12, (uint)page);
        phaseStarted = DateTime.UtcNow;
    }

    private void SelectSubPage(AtkUnitBase* addon, int sub)
    {
        Fire(addon, false, 13, (uint)sub);
        shownSub = sub;
        phaseStarted = DateTime.UtcNow;
    }

    // ================================================================ helpers

    private static bool IsReady(AtkUnitBase* a) => a->IsVisible && a->UldManager.LoadedState == AtkLoadState.Loaded;

    private static bool Ready(string name)
    {
        var a = GameUi.Get(name);
        return a != null && IsReady(a);
    }

    /// <summary>FireCallback with one int then uints (what the collectables and scrip windows want).</summary>
    private static void Fire(AtkUnitBase* addon, bool close, int first, params uint[] rest)
    {
        var count = 1 + rest.Length;
        var vals = stackalloc AtkValue[count];
        vals[0] = default;
        vals[0].Type = ValueType.Int;
        vals[0].Int = first;
        for (var i = 0; i < rest.Length; i++)
        {
            vals[i + 1] = default;
            vals[i + 1].Type = ValueType.UInt;
            vals[i + 1].UInt = rest[i];
        }
        addon->FireCallback((uint)count, vals, close);
    }

    private void CloseMenus()
    {
        var ss = GameUi.Get("SelectString");
        if (ss != null)
        {
            var rows = GameUi.MenuEntries(ss, false);
            var nothing = rows.FindIndex(r => r.StartsWith("Nothing", StringComparison.OrdinalIgnoreCase) || r.StartsWith("Cancel", StringComparison.OrdinalIgnoreCase));
            if (nothing >= 0) ss->FireCallbackInt(nothing); else ss->FireCallbackInt(-1);
        }
        GameUi.Close("SelectIconString");
        var talk = GameUi.Get("Talk");
        if (talk != null) GameUi.ClickTalk(talk);
        vnav.Stop();
        lastProgress = DateTime.UtcNow;
        phaseStarted = DateTime.UtcNow;
        nextAction = DateTime.UtcNow.AddSeconds(0.8);
    }

    private static void CloseShopWindows()
    {
        foreach (var n in new[] { "CollectablesShop", "InclusionShop" })
        {
            var a = GameUi.Get(n);
            if (a != null) a->Close(true);
        }
    }

    public static int ItemCount(uint itemId)
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : im->GetInventoryItemCount(itemId) + im->GetInventoryItemCount(itemId, true);
    }

    private static int StackSize(uint itemId) =>
        Math.Max(1, (int)(Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.StackSize ?? 1));

    public static int FreeSlots()
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : (int)im->GetEmptySlotsInBag();
    }

    private void Finish(string? note = null)
    {
        CloseShopWindows();
        var msg = $"Scrips: turned in {TurnedIn} (+{ScripsEarned} {ScripName}), bought {Bought} {entry.Name}. " +
                  $"{ScripName}: {ScripData.Scrips(entry.CurrencyItemId):N0}." + (note != null ? " " + note : "");
        Notify(msg);
        Log(msg);
        SaveRunLog(false);
        Set(ScripPhase.Done, msg);
    }

    private void Set(ScripPhase phase, string status)
    {
        Phase = phase;
        Status = status;
        phaseStarted = DateTime.UtcNow;
    }

    private void Fail(string why)
    {
        vnav.Stop();
        Notify(why);
        Log("STOP: " + why);
        SaveRunLog(true);
        Failed = true;
        Set(ScripPhase.Idle, why);
        submitted = null;
        purchase = null;
    }

    private static void Notify(string msg) => Plugin.ChatGui.Print($"[TurnIn] {msg}");

    private static readonly List<string> RunLog = new();

    private static void Log(string msg)
    {
        Plugin.Log.Info($"[TurnIn/Scrips] {msg}");
        lock (RunLog) RunLog.Add($"{DateTime.Now:HH:mm:ss.f} {msg}");
    }

    private static void SaveRunLog(bool failed)
    {
        try
        {
            string text;
            lock (RunLog) { text = string.Join(Environment.NewLine, RunLog); RunLog.Clear(); }
            var dir = System.IO.Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "dumps");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_scrips{(failed ? "_FAILED" : "")}.txt"), text);
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Couldn't write run log"); }
    }
}
