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
using Lumina.Excel.Sheets;
using MateriaManager.Core;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MateriaManager;

/// <summary>
/// Sells low-grade materia you have fewer than five of (can't make a transmute batch). Sells from
/// your bags through the item's own right-click menu: "Sell" while a vendor's shop is open, or
/// "Have Retainer Sell Items" while a retainer is open. "Go to the nearest vendor" walks to the
/// closest merchant in sight (vnavmesh), opens their shop, then sells.
/// </summary>
public sealed unsafe class Purger : IDisposable
{
    public enum Phase { Idle, Walking, Opening, Selling }

    private const uint GilShopFirst = 262144, GilShopLast = 327679;
    private readonly Configuration config;
    private readonly VNavIpc vnav = new();
    private DateTime nextAction = DateTime.MinValue, phaseStarted, talkedAt = DateTime.MinValue, lastProgress;
    private float bestDistance = float.MaxValue;
    private ulong vendorId;
    private int tries, sold;
    private (InventoryType Type, int Slot, uint Item)? current;

    public Phase State { get; private set; } = Phase.Idle;
    public string Status { get; private set; } = "";
    public bool Busy => State != Phase.Idle;

    public Purger(Configuration config)
    {
        this.config = config;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    /// <summary>Bag stacks to sell: below the grade, fewer than N owned (bags + saddlebag + retainer as last seen), not protected.</summary>
    public List<(MateriaInfo Info, int InBags, int Owned)> Candidates()
    {
        var all = MateriaData.All();
        var bags = MateriaData.Count(MateriaData.Bags);
        var saddle = MateriaData.Count(MateriaData.Saddlebag);
        var owned = new Dictionary<uint, int>();
        foreach (var d in new[] { bags, saddle, config.RetainerSeen })
            foreach (var (id, n) in d) owned[id] = owned.GetValueOrDefault(id) + n;
        var gradeTotals = owned.Where(o => all.ContainsKey(o.Key)).GroupBy(o => all[o.Key].Grade).ToDictionary(g => g.Key, g => g.Sum(x => x.Value));

        var result = new List<(MateriaInfo, int, int)>();
        foreach (var (id, n) in bags)
        {
            var m = all[id];
            if (m.Grade >= config.PurgeBelowGrade) continue;
            if (!config.PurgeKinds.Has(m.Kind)) continue;
            var total = owned.GetValueOrDefault(id);
            if (total >= config.PurgeUnder) continue;
            if (config.PurgeSpareMixable && gradeTotals.GetValueOrDefault(m.Grade) >= 5) continue;
            if (config.Rules.TryGetValue(id, out var r) && r.Keep > 0) continue;                 // you asked to keep some
            if (config.RetainerRules.TryGetValue(id, out var rr) && rr.Custom && !rr.Skip) continue; // you set a retainer target
            result.Add((m, n, total));
        }
        return result.OrderBy(c => c.Item1.Grade).ThenBy(c => c.Item1.Type).ToList();
    }

    public static bool ShopOpen => GameUi.IsOpen("Shop");
    public static bool RetainerOpen => Storage.RetainerOpen;

    public void SellHere()
    {
        if (!ShopOpen && !RetainerOpen) { Status = "Open a vendor's shop or a retainer first (or use Go to the nearest vendor)."; return; }
        Begin(Phase.Selling, "Selling...");
    }

    public void GoToVendor()
    {
        var v = NearestVendor();
        if (v == null) { Status = "No merchant in sight. Walk near one (or open a retainer) and try again."; return; }
        vendorId = v.GameObjectId;
        bestDistance = float.MaxValue; lastProgress = DateTime.UtcNow; talkedAt = DateTime.MinValue;
        Begin(Phase.Walking, $"Walking to {v.Name}...");
    }

    public void Stop() { vnav.Stop(); State = Phase.Idle; Status = $"Stopped ({sold} sold)."; }

    private void Begin(Phase p, string status) { State = p; Status = status; phaseStarted = DateTime.UtcNow; tries = 0; sold = p == Phase.Selling || p == Phase.Walking ? 0 : sold; current = null; menuFires = 0; stalls = 0; sellProgress = DateTime.UtcNow; occupiedSince = DateTime.MinValue; }

    private void Fail(string why) { vnav.Stop(); State = Phase.Idle; Status = why; Plugin.ChatGui.PrintError($"[Materia] {why}"); }

    /// <summary>The closest event NPC whose data includes a gil shop.</summary>
    private static IGameObject? NearestVendor()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return null;
        var npcs = Plugin.DataManager.GetExcelSheet<ENpcBase>();
        return Plugin.ObjectTable
            .Where(o => o.ObjectKind == ObjectKind.EventNpc && o.IsTargetable && npcs.GetRowOrDefault(o.BaseId) is { } row &&
                        row.ENpcData.Any(d => d.RowId is >= GilShopFirst and <= GilShopLast))
            .OrderBy(o => Vector3.Distance(o.Position, me.Position))
            .FirstOrDefault();
    }

    private void OnUpdate(IFramework framework)
    {
        if (!Busy || DateTime.UtcNow < nextAction) return;
        try
        {
            switch (State)
            {
                case Phase.Walking: TickWalk(); break;
                case Phase.Opening: TickOpen(); break;
                case Phase.Selling: TickSell(); break;
            }
        }
        catch (Exception ex) { Fail("Error: " + ex.Message); Plugin.Log.Error(ex, "Purge failed"); }
    }

    private void TickWalk()
    {
        nextAction = DateTime.UtcNow.AddSeconds(0.5);
        var me = Plugin.ObjectTable.LocalPlayer;
        var v = Plugin.ObjectTable.FirstOrDefault(o => o.GameObjectId == vendorId);
        if (me == null || v == null) { Fail("Lost sight of the vendor."); return; }
        if (Vector3.Distance(me.Position, v.Position) <= 4.5f) { vnav.Stop(); Begin(Phase.Opening, $"Opening {v.Name}'s shop..."); return; }
        if (!vnav.IsReady) return;
        var target = vnav.NearestPoint(v.Position, 6, 6) ?? v.Position;
        var dist = Vector3.Distance(me.Position, target);
        if (dist < bestDistance - 0.5f) { bestDistance = dist; lastProgress = DateTime.UtcNow; }
        if ((DateTime.UtcNow - lastProgress).TotalSeconds > 10) { Fail("vnavmesh isn't getting closer to the vendor."); return; }
        if (!vnav.IsBusy) vnav.MoveCloseTo(target, 1f);
    }

    private void TickOpen()
    {
        nextAction = DateTime.UtcNow.AddSeconds(0.6);
        if (ShopOpen) { Begin(Phase.Selling, "Selling..."); return; }
        if (GameUi.Get("Talk") is var talk && talk != null) { GameUi.ClickTalk(talk); return; }
        var menu = GameUi.Get("SelectString");
        var icon = false;
        if (menu == null) { menu = GameUi.Get("SelectIconString"); icon = menu != null; }
        if (menu != null && talkedAt != DateTime.MinValue)
        {
            var rows = GameUi.MenuEntries(menu, icon);
            var row = rows.FindIndex(r => r.Contains("Purchase", StringComparison.OrdinalIgnoreCase) || r.Contains("Buy", StringComparison.OrdinalIgnoreCase) ||
                                          r.Contains("Shop", StringComparison.OrdinalIgnoreCase) || r.Contains("Trade", StringComparison.OrdinalIgnoreCase));
            if (row < 0) row = 0;   // most merchants: the first entry is their wares
            menu->FireCallbackInt(row);
            nextAction = DateTime.UtcNow.AddSeconds(1.2);
            return;
        }
        if (talkedAt != DateTime.MinValue && (DateTime.UtcNow - talkedAt).TotalSeconds < 4) return;
        if (++tries > 3) { Fail("The vendor's shop didn't open."); return; }
        var v = Plugin.ObjectTable.FirstOrDefault(o => o.GameObjectId == vendorId);
        if (v == null) { Fail("Lost sight of the vendor."); return; }
        TargetSystem.Instance()->InteractWithObject((GameObjectStruct*)v.Address, true);
        talkedAt = DateTime.UtcNow;
        nextAction = DateTime.UtcNow.AddSeconds(1.5);
    }

    private int menuFires, stalls;
    private DateTime sellProgress = DateTime.UtcNow, occupiedSince = DateTime.MinValue;

    private void TickSell()
    {
        nextAction = DateTime.UtcNow.AddSeconds(0.5);
        if (!ShopOpen && !RetainerOpen) { Fail($"Shop/retainer closed ({sold} sold)."); return; }
        if (sold == 0 && current == null && menuFires == 0) sellProgress = DateTime.UtcNow;

        // Watchdog: nothing sold for 8 s -> save dumps, close stray menus and start the item over.
        if ((DateTime.UtcNow - sellProgress).TotalSeconds > 8)
        {
            stalls++;
            Plugin.Log.Warning($"[Purge] stall #{stalls}: item={current?.Item} tries={tries} menuFires={menuFires} occ39={Plugin.Condition[ConditionFlag.Occupied39]}");
            foreach (var n in new[] { "ContextMenu", "SelectYesno", "InputNumeric", "Shop" }) GameUi.Dump(n);
            foreach (var n in new[] { "ContextMenu", "SelectYesno", "InputNumeric" }) GameUi.Close(n);
            if (stalls >= 3) { Fail($"Selling stalled ({sold} sold). Dumps saved."); return; }
            current = null; menuFires = 0; tries = 0; sellProgress = DateTime.UtcNow;
            nextAction = DateTime.UtcNow.AddSeconds(1);
            return;
        }

        // The game is mid-transaction; wait, but not forever.
        if (Plugin.Condition[ConditionFlag.Occupied39])
        {
            if (occupiedSince == DateTime.MinValue) occupiedSince = DateTime.UtcNow;
            if ((DateTime.UtcNow - occupiedSince).TotalSeconds < 3) return;
        }
        else occupiedSince = DateTime.MinValue;

        var yesno = GameUi.Get("SelectYesno");
        if (yesno != null) { yesno->FireCallbackInt(0); nextAction = DateTime.UtcNow.AddSeconds(0.8); return; }
        var num = GameUi.Get("InputNumeric");
        if (num != null)
        {
            var q = 99;
            if (current is { } cq && InventoryManager.Instance()->GetInventorySlot(cq.Type, cq.Slot) is var sq && sq != null) q = (int)sq->Quantity;
            GameUi.Dump("InputNumeric"); num->FireCallbackInt(q); nextAction = DateTime.UtcNow.AddSeconds(0.8); return;
        }

        // Did the last one go?
        if (current is { } c)
        {
            var slot = InventoryManager.Instance()->GetInventorySlot(c.Type, c.Slot);
            if (slot == null || slot->ItemId != c.Item)
            {
                sold++; current = null; tries = 0; menuFires = 0; stalls = 0; sellProgress = DateTime.UtcNow;
                Plugin.Log.Info($"[Purge] sold {c.Item} ({sold})");
                GameUi.Close("ContextMenu");
                return;
            }
        }

        var menu = GameUi.Get("ContextMenu");
        if (menu != null)
        {
            // Clicked Sell already and the menu is still up: close it and reopen it for the item.
            if (menuFires >= 2) { Plugin.Log.Warning("[Purge] menu stayed open after Sell; closing"); GameUi.Dump("ContextMenu"); menu->FireCallbackInt(-1); menuFires = 0; nextAction = DateTime.UtcNow.AddSeconds(0.8); return; }
            var want = ShopOpen ? new[] { "Sell" } : new[] { "Have Retainer Sell Items", "Sell" };
            var index = -1;
            foreach (var w in want)
            {
                for (var i = 7; i < menu->AtkValuesCount && index < 0; i++)
                    if (GameUi.Str(menu, i).Equals(w, StringComparison.OrdinalIgnoreCase)) index = i - 8;
                if (index >= 0) break;
            }
            if (index < 0) { GameUi.Dump("ContextMenu"); menu->FireCallbackInt(-1); Fail("No Sell option in the item menu (dump saved)."); return; }
            GameUi.Fire(menu, 0, index, 0);
            menuFires++;
            nextAction = DateTime.UtcNow.AddSeconds(1);
            return;
        }

        if (current != null && ++tries > 4) { Fail("An item wouldn't sell; stopped."); return; }
        if (current == null)
        {
            var targets = Candidates().Select(x => x.Info.ItemId).ToHashSet();
            current = null;
            var im = InventoryManager.Instance();
            foreach (var t in MateriaData.Bags)
            {
                var cont = im->GetInventoryContainer(t);
                if (cont == null || !cont->IsLoaded) continue;
                for (var i = 0; i < cont->Size && current == null; i++)
                {
                    var s = cont->GetInventorySlot(i);
                    if (s != null && targets.Contains(s->ItemId)) current = (t, i, s->ItemId);
                }
                if (current != null) break;
            }
            if (current == null)
            {
                State = Phase.Idle;
                Status = $"Purge done: {sold} stack(s) sold.";
                Plugin.ChatGui.Print($"[Materia] {Status}");
                return;
            }
        }
        var cur = current.Value;
        menuFires = 0;
        AgentInventoryContext.Instance()->OpenForItemSlot(cur.Type, cur.Slot, 0, InventoryAddonId());
        nextAction = DateTime.UtcNow.AddSeconds(0.8);
    }

    private static uint InventoryAddonId()
    {
        var mgr = RaptureAtkUnitManager.Instance();
        foreach (var name in new[] { "Inventory", "InventoryLarge", "InventoryExpansion" })
        {
            var a = mgr->GetAddonByName(name);
            if (a != null && a->IsVisible) return a->Id;
        }
        AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory)->Show();
        var inv = mgr->GetAddonByName("Inventory");
        return inv != null ? (uint)inv->Id : 0u;
    }
}
