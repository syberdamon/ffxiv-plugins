using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TurnInManager.Core;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace TurnInManager.GrandCompany;

public enum GcPhase { Idle, Retrieving, Traveling, ClearingMenus, Walking, Talking, Delivering, Buying, Done }

public enum GcMode { Full, BuyOnly, StripOnly }

/// <summary>
/// One Grand Company run:
///  1. Retrieve materia from gear that will be turned in (turning it in with materia loses it).
///  2. Travel to your GC (Lifestream "/li gc", if installed) and walk to the Personnel Officer.
///  3. Expert Delivery: hand in allowed gear, one at a time, verified against bags and seals.
///  4. Before a delivery would push seals over the cap: walk to the Quartermaster, buy the
///     purchase list up to its stock targets, come back, keep delivering.
///  5. Optionally spend down at the end.
/// Callback layouts follow community references (see THIRD_PARTY_NOTICES); every click that spends
/// something is checked against the result, and anything unexpected stops the run.
/// </summary>
public sealed unsafe class GcRunner : IDisposable
{
    private readonly Configuration config;
    private readonly VNavIpc vnav;
    private readonly LifestreamIpc lifestream;

    public GcPhase Phase { get; private set; } = GcPhase.Idle;
    public string Status { get; private set; } = "";
    public bool Busy => Phase is not (GcPhase.Idle or GcPhase.Done);
    public int Delivered { get; private set; }
    public int SealsEarned { get; private set; }
    public List<string> Bought { get; } = new();

    private GcData.GcInfo gc = null!;
    private string npcName = "";
    private GcPhase afterTalk;
    private DateTime phaseStarted, nextAction;
    private float bestDistance;
    private DateTime lastProgress;
    private int tries;

    // retrieval
    private Queue<GearCandidate> retrieveQueue = new();
    private GearCandidate? retrieving;

    // delivery
    private GrandCompanyItemKey? pending;
    private int sealsBefore;
    private bool spendAfterDeliveries;

    // buying
    private int ruleIndex;
    private int tabCombo;
    private int countBefore, sealsBeforeBuy;
    private bool awaitingPurchase;
    private DateTime purchaseAt;

    private readonly record struct GrandCompanyItemKey(uint ItemId, InventoryType Container, int Slot, int Seals, string Name);

    // Exchange window radio buttons: rank tabs 37-39, categories 44-47 (from community references).
    private static readonly (uint Rank, uint Category)[] TabCombos =
        (from r in new uint[] { 37, 38, 39 } from c in new uint[] { 44, 45, 46, 47 } select (r, c)).ToArray();

    public GcRunner(Configuration config, VNavIpc vnav, LifestreamIpc lifestream)
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

    public string? Blocker()
    {
        if (Plugin.ObjectTable.LocalPlayer == null) return "Not logged in.";
        if (Busy) return "Already running.";
        if (GcData.Current() == null) return "You're not in a Grand Company.";
        if (Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.BoundByDuty]) return "In combat or a duty.";
        if (!vnav.IsLoaded) return "vnavmesh isn't installed or loaded.";
        if (GcData.FreeSlots() < 2) return "Keep at least 2 free bag slots (materia and purchases need room).";
        return null;
    }

    public void Start(GcMode mode = GcMode.Full)
    {
        this.mode = mode;
        var buyOnly = mode == GcMode.BuyOnly;
        if (Blocker() is { } why) { Notify(why); return; }
        gc = GcData.Current()!;
        Delivered = 0; SealsEarned = 0; Bought.Clear();
        spendAfterDeliveries = config.SpendAtEnd;

        var candidates = GcData.Scan(config).Where(c => c.Allowed).ToList();
        Log($"Start: {gc.Name}, rank {GcData.Rank()}, seals {GcData.Seals()}/{GcData.SealCap()}, {candidates.Count} allowed items, mode {mode}");

        if (buyOnly) { GoTo(gc.Quartermaster, GcPhase.Buying); return; }
        retrieveQueue = new Queue<GearCandidate>(config.RetrieveMateria || mode == GcMode.StripOnly
            ? candidates.Where(c => c.MateriaCount > 0) : Enumerable.Empty<GearCandidate>());
        if (mode == GcMode.StripOnly && retrieveQueue.Count == 0) { Notify("No materia to strip from items that pass your filters."); return; }
        if (retrieveQueue.Count > 0)
        {
            Set(GcPhase.Retrieving, $"Retrieving materia from {retrieveQueue.Count} item(s)...");
            return;
        }
        GoToDeliverOrBuyFirst();
    }

    /// <summary>Seals already at/near the cap: go to the Quartermaster first instead of the Personnel Officer.</summary>
    private void GoToDeliverOrBuyFirst()
    {
        if (GcData.Seals() + NearCapMargin > GcData.SealCap())
        {
            if (config.BuyBeforeCap && NeedsAnyPurchase())
            {
                Log($"Seals near the cap ({GcData.Seals()}/{GcData.SealCap()}): buying first.");
                GoTo(gc.Quartermaster, GcPhase.Buying);
                return;
            }
            if (GcData.Seals() >= GcData.SealCap()) { Fail($"Seals are full ({GcData.Seals()}/{GcData.SealCap()}) and nothing on the purchase list needs buying."); return; }
        }
        GoTo(gc.Personnel, GcPhase.Delivering);
    }

    public void Stop(string why = "Stopped.")
    {
        vnav.Stop();
        if (lifestream.IsBusy) lifestream.Abort();
        Set(GcPhase.Idle, why);
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
                case GcPhase.Retrieving: TickRetrieve(); break;
                case GcPhase.Traveling: TickTravel(); break;
                case GcPhase.ClearingMenus: TickClearMenus(); break;
                case GcPhase.Walking: TickWalk(); break;
                case GcPhase.Talking: TickTalk(); break;
                case GcPhase.Delivering: TickDeliver(); break;
                case GcPhase.Buying: TickBuy(); break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Turn-in run failed");
            Fail("Something went wrong (see /xllog).");
        }
    }

    private static double Timeout(GcPhase p) => p switch
    {
        GcPhase.Retrieving => 60,   // per item (reset on progress)
        GcPhase.Traveling => 180,
        GcPhase.Walking => 120,
        _ => 30,
    };

    // ================================================================ 1. materia

    private void TickRetrieve()
    {
        if (retrieving == null)
        {
            if (retrieveQueue.Count == 0)
            {
                if (mode == GcMode.StripOnly) { Finish(); return; }
                GoToDeliverOrBuyFirst();
                return;
            }
            retrieving = retrieveQueue.Dequeue();
            phaseStarted = DateTime.UtcNow;
            Status = $"Retrieving materia: {retrieving.Name}";
            Log($"Retrieve materia: {retrieving.Name} ({retrieving.MateriaCount}) in {retrieving.Container}/{retrieving.Slot}");
        }

        var item = retrieving;
        var slot = GcData.Slot(item.Container, item.Slot);
        if (slot == null || slot->ItemId != item.ItemId) { Fail($"{item.Name} moved; stopping to be safe."); return; }
        if (GcData.MateriaCount(item.Container, item.Slot) == 0)
        {
            Log($"  done: {item.Name}");
            retrieving = null;
            nextAction = DateTime.UtcNow.AddSeconds(0.5);
            return;
        }
        if (GcData.FreeSlots() < 1) { Fail("Bags are full; can't retrieve materia."); return; }

        // Dialog: confirm ("Begin").
        var dialog = GameUi.Get("MateriaRetrieveDialog");
        if (dialog != null)
        {
            GameUi.Fire(dialog, 0);
            nextAction = DateTime.UtcNow.AddSeconds(3);   // retrieval animation
            return;
        }

        // Context menu: pick "Retrieve Materia".
        var menu = GameUi.Get("ContextMenu");
        if (menu != null)
        {
            var index = -1;
            for (var i = 7; i < menu->AtkValuesCount; i++)
                if (GameUi.Str(menu, i).Contains("Retrieve Materia", StringComparison.OrdinalIgnoreCase)) { index = i - 8; break; }
            if (index < 0) { GameUi.Dump("ContextMenu"); Fail($"No 'Retrieve Materia' in the menu for {item.Name}."); return; }
            GameUi.Fire(menu, 0, index, 0, 0);
            nextAction = DateTime.UtcNow.AddSeconds(1.2);
            return;
        }

        // Open the item's context menu.
        if (++tries > 20) { Fail($"Couldn't open the menu for {item.Name}."); return; }
        var ownerId = InventoryAddonId();
        AgentInventoryContext.Instance()->OpenForItemSlot(item.Container, item.Slot, 0, ownerId);
        nextAction = DateTime.UtcNow.AddSeconds(1);
    }

    private static uint InventoryAddonId()
    {
        var mgr = RaptureAtkUnitManager.Instance();
        foreach (var name in new[] { "Inventory", "InventoryLarge", "InventoryExpansion" })
        {
            var a = mgr->GetAddonByName(name);
            if (a != null && a->IsVisible) return a->Id;
        }
        // Open the inventory so the menu has an owner.
        AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory)->Show();
        var inv = mgr->GetAddonByName("Inventory");
        return inv != null ? (uint)inv->Id : 0u;
    }

    // ================================================================ 2. travel + walk + talk

    private void GoTo(string npc, GcPhase then)
    {
        npcName = npc;
        afterTalk = then;
        tries = 0;
        talkedAt = DateTime.MinValue;
        // Closing a GC window can bring the previous NPC's menu back up. Clear any open menus
        // before talking to the next NPC, so we never answer one meant for someone else.
        if (AnyMenuOpen()) { Set(GcPhase.ClearingMenus, "Closing menus..."); return; }
        ContinueGoTo();
    }

    private DateTime talkedAt = DateTime.MinValue;
    private GcMode mode;

    private static readonly string[] MenuAddons =
        { "SelectString", "SelectIconString", "Talk", "GrandCompanySupplyList", "GrandCompanySupplyReward", "GrandCompanyExchange", "SelectYesno", "ShopExchangeCurrencyDialog" };

    private static bool AnyMenuOpen() => MenuAddons.Any(GameUi.IsOpen);

    private void TickClearMenus()
    {
        foreach (var name in MenuAddons)
        {
            var a = GameUi.Get(name);
            if (a == null) continue;
            if (name == "SelectString")
            {
                // Pick "Nothing."/"Cancel" if there is one, else close.
                var rows = GameUi.MenuEntries(a, false);
                var nothing = rows.FindIndex(r => r.StartsWith("Nothing", StringComparison.OrdinalIgnoreCase) || r.StartsWith("Cancel", StringComparison.OrdinalIgnoreCase));
                if (nothing >= 0) a->FireCallbackInt(nothing); else a->FireCallbackInt(-1);
            }
            else if (name == "Talk") GameUi.ClickTalk(a);
            else a->FireCallbackInt(-1);
            nextAction = DateTime.UtcNow.AddSeconds(0.6);
            return;
        }
        ContinueGoTo();
    }

    private void ContinueGoTo()
    {
        var npc = npcName;
        var here = Plugin.ClientState.TerritoryType;
        if (NpcLocator.InTerritory(npc, here) != null || FindNpc() != null) { StartWalk(); return; }

        if (config.UseLifestream && lifestream.IsLoaded)
        {
            Log($"Lifestream: /li gc (to {npc})");
            lifestream.Execute("gc");
            Set(GcPhase.Traveling, "Traveling to your Grand Company (Lifestream)...");
            nextAction = DateTime.UtcNow.AddSeconds(2);
            return;
        }
        Fail($"Go to your Grand Company first (or install Lifestream so I can take you).");
    }

    private void TickTravel()
    {
        if (lifestream.IsBusy || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]) return;
        if (NpcLocator.InTerritory(npcName, Plugin.ClientState.TerritoryType) != null || FindNpc() != null) { StartWalk(); return; }
        if ((DateTime.UtcNow - phaseStarted).TotalSeconds > 20 && !lifestream.IsBusy) Fail("Lifestream finished somewhere else.");
    }

    private void StartWalk()
    {
        Set(GcPhase.Walking, $"Walking to the {npcName}...");
        bestDistance = float.MaxValue;
        lastProgress = DateTime.UtcNow;
    }

    private IGameObject? FindNpc() =>
        Plugin.ObjectTable.FirstOrDefault(o => o.ObjectKind == ObjectKind.EventNpc && o.Name.TextValue.Equals(npcName, StringComparison.OrdinalIgnoreCase));

    private void TickWalk()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null || !vnav.IsReady) return;
        var npc = FindNpc();
        if (npc != null && Vector3.Distance(me.Position, npc.Position) <= 4.5f) { vnav.Stop(); Set(GcPhase.Talking, $"Talking to the {npcName}..."); tries = 0; return; }

        Vector3 goal;
        if (npc != null) goal = npc.Position;
        else if (NpcLocator.InTerritory(npcName, Plugin.ClientState.TerritoryType) is { } spot) goal = spot;
        else { Fail($"Can't find the {npcName} here."); return; }
        var target = vnav.NearestPoint(goal, 6, 6) ?? goal;
        var dist = Vector3.Distance(me.Position, target);
        if (dist <= 1.5f && npc != null && Vector3.Distance(me.Position, npc.Position) <= 8) { vnav.Stop(); Set(GcPhase.Talking, $"Talking to the {npcName}..."); tries = 0; return; }
        if (dist < bestDistance - 0.5f) { bestDistance = dist; lastProgress = DateTime.UtcNow; }
        if ((DateTime.UtcNow - lastProgress).TotalSeconds > 8) { Fail($"vnavmesh isn't getting closer to the {npcName}."); return; }
        if (!vnav.IsBusy) vnav.MoveCloseTo(target, 1f);
        nextAction = DateTime.UtcNow.AddSeconds(0.5);
    }

    private void TickTalk()
    {
        // Already in the right window?
        if (afterTalk == GcPhase.Delivering && GameUi.IsOpen("GrandCompanySupplyList")) { Set(GcPhase.Delivering, "Delivering..."); return; }
        if (afterTalk == GcPhase.Buying && GameUi.IsOpen("GrandCompanyExchange")) { BeginBuying(); return; }

        // Menu: pick the GC option, but only one that opened after we talked to this NPC.
        var menu = GameUi.Get("SelectString");
        if (menu != null && talkedAt == DateTime.MinValue)
        {
            Log("  a menu was already open before talking; closing it");
            Set(GcPhase.ClearingMenus, "Closing menus...");
            return;
        }
        if (menu != null)
        {
            var rows = GameUi.MenuEntries(menu, false);
            Log($"  {npcName} menu: {string.Join(" | ", rows)}");
            // Personnel Officer: "Undertake supply and provisioning missions." (option 1).
            // Quartermaster normally opens the shop directly; if it shows a menu, look for the exchange.
            var want = afterTalk == GcPhase.Delivering
                ? new[] { "Undertake supply and provisioning", "supply and provisioning missions" }
                : new[] { "Purchase", "Exchange" };
            var row = rows.FindIndex(r => want.Any(w => r.Contains(w, StringComparison.OrdinalIgnoreCase)));
            if (row < 0) { Fail($"Menu has none of [{string.Join(", ", want)}]: {string.Join(" | ", rows)}"); return; }
            menu->FireCallbackInt(row);
            nextAction = DateTime.UtcNow.AddSeconds(1);
            return;
        }
        var talk = GameUi.Get("Talk");
        if (talk != null) { GameUi.ClickTalk(talk); nextAction = DateTime.UtcNow.AddSeconds(0.5); return; }

        if (++tries > 3) { Fail($"The {npcName} didn't open a menu."); return; }
        var npc = FindNpc();
        if (npc == null) { Fail($"Lost sight of the {npcName}."); return; }
        TargetSystem.Instance()->InteractWithObject((GameObjectStruct*)npc.Address, true);
        talkedAt = DateTime.UtcNow;
        nextAction = DateTime.UtcNow.AddSeconds(2);
    }

    // ================================================================ 3. expert delivery

    private void TickDeliver()
    {
        // Confirmations first.
        var reward = (AddonGrandCompanySupplyReward*)GameUi.Get("GrandCompanySupplyReward");
        if (reward != null)
        {
            GameUi.ClickButton((AtkUnitBase*)reward, reward->DeliverButton);
            nextAction = DateTime.UtcNow.AddSeconds(0.8);
            return;
        }
        var yesNo = GameUi.Get("SelectYesno");
        if (yesNo != null)
        {
            Log($"  Yes/No during delivery: \"{GameUi.YesNoText()}\" -> Yes");
            yesNo->FireCallbackInt(0);
            nextAction = DateTime.UtcNow.AddSeconds(0.8);
            return;
        }

        var list = GameUi.Get("GrandCompanySupplyList");
        if (list == null) { Set(GcPhase.Talking, "Reopening the delivery list..."); afterTalk = GcPhase.Delivering; tries = 0; return; }

        // A delivery in flight: done when the item left its slot and seals went up.
        if (pending is { } p)
        {
            var slot = GcData.Slot(p.Container, p.Slot);
            if (slot == null || slot->ItemId != p.ItemId)
            {
                var gained = GcData.Seals() - sealsBefore;
                Delivered++; SealsEarned += Math.Max(0, gained);
                Log($"  delivered {p.Name}: +{gained} seals ({GcData.Seals()}/{GcData.SealCap()})");
                pending = null;
                phaseStarted = DateTime.UtcNow;
                nextAction = DateTime.UtcNow.AddSeconds(0.5);
                return;
            }
            if ((DateTime.UtcNow - phaseStarted).TotalSeconds > 8) { GameUi.Dump("GrandCompanySupplyList"); Fail($"{p.Name} wasn't taken (details in /xllog)."); }
            return;
        }

        // Expert Delivery tab (index 2).
        if (GameUi.Int(list, 0) != 2) return; // still loading
        if (GameUi.Int(list, 5) != 2) { GameUi.Fire(list, 0, 2); nextAction = DateTime.UtcNow.AddSeconds(0.6); return; }

        // Pick the next allowed item the game offers.
        var allowed = GcData.Scan(config).Where(c => c.Allowed && c.MateriaCount == 0).ToDictionary(c => (c.Container, c.Slot));
        var agent = AgentGrandCompanySupply.Instance();
        GrandCompanyItemKey? next = null;
        for (var i = 0; i < agent->NumItems; i++)
        {
            var it = agent->ItemArray[i];
            if (it.IsBonusReward || it.ExpReward > 0 || it.SealReward <= 0) continue;
            if (!allowed.TryGetValue((it.Inventory, (int)it.Slot), out var cand) || cand.ItemId != it.ItemId) continue;
            next = new GrandCompanyItemKey(it.ItemId, it.Inventory, it.Slot, it.SealReward, cand.Name);
            break;
        }

        if (next == null)
        {
            Log($"Deliveries done: {Delivered} items, +{SealsEarned} seals.");
            list->FireCallbackInt(-1);
            if (spendAfterDeliveries && config.Purchases.Any(r => r.Enabled)) { spendAfterDeliveries = false; GoTo(gc.Quartermaster, GcPhase.Buying); return; }
            Finish();
            return;
        }

        // Seal cap guard.
        var reward2 = (int)(next.Value.Seals * GcData.SealMultiplier());
        if (GcData.Seals() + reward2 > GcData.SealCap())
        {
            list->FireCallbackInt(-1);
            if (config.BuyBeforeCap && NeedsAnyPurchase()) { Log($"Seals would cap ({GcData.Seals()} + {reward2} > {GcData.SealCap()}): buying first."); GoTo(gc.Quartermaster, GcPhase.Buying); return; }
            Fail($"Seals would cap ({GcData.Seals()} + {reward2} > {GcData.SealCap()}) and nothing on the purchase list needs buying.");
            return;
        }

        // Find its row in the window: count [6]; per row: container [345+i], slot [385+i], item [425+i].
        var count = GameUi.Int(list, 6);
        var row = -1;
        for (var i = 0; i < count && i < 40; i++)
            if (GameUi.UInt(list, 425 + i) == next.Value.ItemId && GameUi.UInt(list, 345 + i) == (uint)next.Value.Container &&
                GameUi.UInt(list, 385 + i) == (uint)next.Value.Slot) { row = i; break; }
        if (row < 0) { GameUi.Dump("GrandCompanySupplyList"); Fail($"{next.Value.Name} isn't in the delivery list (details in /xllog)."); return; }

        Status = $"Delivering {next.Value.Name} (+{reward2} seals)...";
        pending = next;
        sealsBefore = GcData.Seals();
        phaseStarted = DateTime.UtcNow;
        GameUi.Fire(list, 1, row);
        nextAction = DateTime.UtcNow.AddSeconds(0.8);
    }

    // ================================================================ 4. buying

    private bool NeedsAnyPurchase() =>
        config.Purchases.Any(r => r.Enabled && r.ItemId != 0 && GcData.Count(r.ItemId) < r.Target);

    private void BeginBuying()
    {
        ruleIndex = 0;
        tabCombo = -1;
        awaitingPurchase = false;
        Set(GcPhase.Buying, "Buying with seals...");
    }

    private void TickBuy()
    {
        var shop = GameUi.Get("GrandCompanyExchange");

        // 1. Confirmation ("Exchange 64,200 seals for 321 ventures?") always first: the quantity
        //    window stays open underneath it.
        var yesNo = GameUi.Get("SelectYesno");
        if (yesNo != null)
        {
            Log($"  Yes/No during purchase: \"{GameUi.YesNoText()}\" -> Yes");
            yesNo->FireCallbackInt(0);
            nextAction = DateTime.UtcNow.AddSeconds(0.8);
            return;
        }

        // 2. Quantity window (Ventures): fill it in and press Exchange once per purchase.
        var currency = GameUi.Get("ShopExchangeCurrencyDialog");
        if (currency != null && awaitingPurchase && !currencyHandled)
        {
            currencyHandled = true;
            var rule = config.Purchases[ruleIndex];
            var cost = Math.Max(1, lastCost);
            var dialogQty = Math.Min(rule.Target - GcData.Count(rule.ItemId), (GcData.Seals() - config.SealReserve) / cost);
            dialogQty = Math.Clamp(dialogQty, 1, 999);
            var numeric = (AtkComponentNumericInput*)currency->UldManager.NodeList[8]->GetComponent();
            if (numeric != null) numeric->SetValue(dialogQty);
            GameUi.ClickNode(currency, 17); // Exchange
            Log($"  quantity dialog: {dialogQty}");
            nextAction = DateTime.UtcNow.AddSeconds(0.8);
            return;
        }

        if (shop == null) { Set(GcPhase.Talking, "Reopening the seal shop..."); afterTalk = GcPhase.Buying; tries = 0; return; }

        // A purchase in flight: done when seals dropped and the count rose.
        if (awaitingPurchase)
        {
            var rule = config.Purchases[ruleIndex];
            if (GcData.Seals() < sealsBeforeBuy && GcData.Count(rule.ItemId) > countBefore)
            {
                var got = GcData.Count(rule.ItemId) - countBefore;
                Bought.Add($"{got} x {rule.Name}");
                Log($"  bought {got} x {rule.Name} (-{sealsBeforeBuy - GcData.Seals()} seals)");
                awaitingPurchase = false;
                if (currency != null) currency->FireCallbackInt(-1); // close the leftover quantity window
                phaseStarted = DateTime.UtcNow;
                nextAction = DateTime.UtcNow.AddSeconds(0.6);
                return;
            }
            if ((DateTime.UtcNow - purchaseAt).TotalSeconds > 6) { GameUi.Dump("GrandCompanyExchange"); Fail($"Buying {rule.Name} didn't go through (details in /xllog)."); }
            return;
        }

        // Next rule that needs buying and is affordable.
        while (ruleIndex < config.Purchases.Count)
        {
            var r = config.Purchases[ruleIndex];
            if (r.Enabled && r.ItemId != 0 && GcData.Count(r.ItemId) < r.Target && GcData.Seals() - config.SealReserve > 0) break;
            ruleIndex++; tabCombo = -1;
        }
        if (ruleIndex >= config.Purchases.Count)
        {
            shop->FireCallbackInt(-1);
            Log("Buying done.");
            if (pendingDeliveries()) { GoTo(gc.Personnel, GcPhase.Delivering); return; }
            Finish();
            return;
        }

        var rule2 = config.Purchases[ruleIndex];
        // Find it in the list: count [1]; item i: name [17+i], seals [67+i], stackable [267+i], id [317+i], rank [417+i], currency dialog [467+i].
        var n = (int)GameUi.UInt(shop, 1);
        var row = -1;
        for (var i = 0; i < n && i < 50; i++) if (GameUi.UInt(shop, 317 + i) == rule2.ItemId) { row = i; break; }
        if (row < 0)
        {
            // Not on this tab: try the next rank/category combination.
            if (++tabCombo >= TabCombos.Length) { Log($"  {rule2.Name} isn't sold here (or not at your rank); skipping."); ruleIndex++; tabCombo = -1; return; }
            GameUi.ClickNode(shop, TabCombos[tabCombo].Rank);
            GameUi.ClickNode(shop, TabCombos[tabCombo].Category);
            nextAction = DateTime.UtcNow.AddSeconds(0.5);
            return;
        }

        lastCost = (int)GameUi.UInt(shop, 67 + row);
        var stackable = GameUi.Bool(shop, 267 + row);
        var opensDialog = GameUi.Bool(shop, 467 + row);
        var rankOk = GcData.Rank() >= (int)GameUi.UInt(shop, 417 + row);
        var afford = (GcData.Seals() - config.SealReserve) / Math.Max(1, lastCost);
        var need = rule2.Target - GcData.Count(rule2.ItemId);
        var qty = Math.Min(need, afford);
        if (!stackable) qty = Math.Min(qty, 1);
        qty = Math.Min(qty, 99);
        if (!rankOk || qty < 1) { Log($"  {rule2.Name}: {(rankOk ? "can't afford" : "rank too low")}; skipping."); ruleIndex++; tabCombo = -1; return; }

        Status = $"Buying {(opensDialog ? "" : qty + " x ")}{rule2.Name} ({lastCost} seals each)...";
        countBefore = GcData.Count(rule2.ItemId);
        sealsBeforeBuy = GcData.Seals();
        awaitingPurchase = true;
        currencyHandled = false;
        purchaseAt = DateTime.UtcNow;
        // Ventures and similar open a quantity dialog; others take the quantity here.
        GameUi.Fire(shop, 0, row, opensDialog ? 1 : qty, GameUi.V.ZeroValue, rankOk, opensDialog, GameUi.V.ZeroValue, GameUi.V.ZeroValue, GameUi.V.ZeroValue);
        nextAction = DateTime.UtcNow.AddSeconds(0.8);
    }

    private int lastCost;
    private bool currencyHandled;

    /// <summary>[Estimate] An expert delivery at high level is worth up to ~3,000 seals.</summary>
    private const int NearCapMargin = 3000;

    private bool pendingDeliveries() =>
        GcData.Scan(config).Any(c => c.Allowed);

    // ================================================================ helpers

    private void Finish()
    {
        var msg = $"Done: delivered {Delivered} item(s) for {SealsEarned} seals" +
                  (Bought.Count > 0 ? $"; bought {string.Join(", ", Bought)}" : "") + $". Seals {GcData.Seals()}/{GcData.SealCap()}.";
        Notify(msg);
        Set(GcPhase.Done, msg);
    }

    private void Set(GcPhase phase, string status)
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
        Set(GcPhase.Idle, why);
        pending = null;
        awaitingPurchase = false;
        retrieving = null;
    }

    private static void Notify(string msg) => Plugin.ChatGui.Print($"[TurnIn] {msg}");
    private static void Log(string msg) => Plugin.Log.Info($"[TurnIn] {msg}");
}
