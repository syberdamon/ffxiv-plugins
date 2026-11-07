using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MaterialStash.Core;

namespace MaterialStash;

/// <summary>
/// The stash run, started by you at the summoning bell (retainer list open). For each retainer that
/// something goes to: select it, "Entrust or withdraw items", move stacks one at a time, close,
/// "Quit", back to the list. A scan does the same walk but only reads. Never touches ventures or sales.
/// Stops at once if AutoRetainer is busy, a window doesn't appear in time, or you press Stop.
/// </summary>
public sealed unsafe class Runner : IDisposable
{
    private enum Phase { Idle, Select, OpenMenu, OpenInventory, Work, CloseInventory, Quit, BackToList }

    private sealed class CrystalOp
    {
        public uint Id;
        public int Amount;
        public bool Deposit;
        public int Before;
        public int Tries;
    }

    private const double PhaseTimeout = 15;

    private readonly Configuration config;
    private readonly Tracker tracker;
    private readonly Planner planner;
    private readonly AutoRetainerIpc autoRetainer;

    private Phase phase = Phase.Idle;
    private DateTime next = DateTime.MinValue, phaseStart = DateTime.MinValue;
    private readonly Queue<ulong> queue = new();
    private readonly HashSet<ulong> visited = new();
    private ulong current;
    private bool scanOnly;
    private int pass;
    private int stacksMoved, crystalsMoved;
    private CrystalOp? crystal;
    private readonly HashSet<uint> skipCrystals = new();
    private (InventoryType Type, int Slot, uint Item, int Qty)? lastMove;
    private int sameMoveCount;
    private readonly HashSet<(InventoryType, int)> skipStacks = new();

    public string Status { get; private set; } = "";
    public bool Busy => phase != Phase.Idle;
    public bool Scanning => Busy && scanOnly;

    public Runner(Configuration config, Tracker tracker, Planner planner, AutoRetainerIpc autoRetainer)
    {
        this.config = config;
        this.tracker = tracker;
        this.planner = planner;
        this.autoRetainer = autoRetainer;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    public static bool BellListOpen => GameUi.IsOpen("RetainerList");

    /// <summary>Why a run can't start right now, or null if it can.</summary>
    public string? CantStart()
    {
        if (Busy) return "Already running.";
        if (!BellListOpen) return "Open the summoning bell (retainer list) first.";
        if (autoRetainer.IsBusy) return "AutoRetainer is busy: press again when it's done.";
        if (tracker.Me() is not { } me || me.Order.Count == 0) return "Retainer list not read yet: wait a second.";
        return null;
    }

    public void Start(bool scan)
    {
        if (CantStart() is { } why) { Status = why; return; }
        var me = tracker.Me()!;
        scanOnly = scan;
        pass = 1;
        stacksMoved = crystalsMoved = 0;
        visited.Clear(); skipStacks.Clear();
        crystal = null; lastMove = null;
        queue.Clear();
        var list = scan ? me.Order : planner.RetainersToVisit();
        if (list.Count == 0)
        {
            Status = "Nothing to stash (or nowhere to put it: check the overflow order).";
            return;
        }
        foreach (var id in list) queue.Enqueue(id);
        Set(Phase.Select);
        Status = scan ? "Scanning retainers..." : "Stashing...";
    }

    public void Stop()
    {
        if (!Busy) return;
        phase = Phase.Idle;
        crystal = null;
        Status = "Stopped.";
        Plugin.ChatGui.Print("[Stash] Stopped.");
    }

    private void Set(Phase p)
    {
        phase = p;
        phaseStart = DateTime.UtcNow;
        next = DateTime.UtcNow.AddSeconds(config.StepDelay);
    }

    private void Fail(string why, string? dump = null)
    {
        if (dump != null) GameUi.Dump(dump, why);
        phase = Phase.Idle;
        crystal = null;
        Status = why;
        Plugin.ChatGui.PrintError($"[Stash] {why}");
    }

    private void Finish()
    {
        phase = Phase.Idle;
        tracker.Save(force: true);
        if (scanOnly)
        {
            Status = $"Scanned {visited.Count} retainer(s).";
        }
        else
        {
            var left = planner.PreviewMaterials();
            var noRoom = left.Count(m => m.To == null);
            Status = $"Stashed {stacksMoved} stack(s)" + (crystalsMoved > 0 ? $" and {crystalsMoved:N0} crystals" : "") +
                     $" on {visited.Count} retainer(s)." + (noRoom > 0 ? $" {noRoom} stack(s) had nowhere to go." : "");
        }
        Plugin.ChatGui.Print($"[Stash] {Status}");
    }

    // ------------------------------------------------------------------ loop

    private void OnUpdate(IFramework framework)
    {
        if (phase == Phase.Idle || DateTime.UtcNow < next) return;
        next = DateTime.UtcNow.AddSeconds(config.StepDelay);
        try
        {
            if ((DateTime.UtcNow - phaseStart).TotalSeconds > PhaseTimeout)
            {
                Fail($"Timed out at step '{phase}' (retainer {tracker.NameOf(current)}).", phase switch
                {
                    Phase.OpenMenu or Phase.Quit => "SelectString",
                    Phase.Select or Phase.BackToList => "RetainerList",
                    _ => Tracker.RetainerInventoryOpen ? "InventoryRetainerLarge" : "SelectString",
                });
                return;
            }
            switch (phase)
            {
                case Phase.Select: TickSelect(); break;
                case Phase.OpenMenu: TickOpenMenu(); break;
                case Phase.OpenInventory: TickOpenInventory(); break;
                case Phase.Work: TickWork(); break;
                case Phase.CloseInventory: TickCloseInventory(); break;
                case Phase.Quit: TickQuit(); break;
                case Phase.BackToList: TickBackToList(); break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Stash run failed");
            Fail("Error (see /xllog).");
        }
    }

    private bool ClickThroughTalk()
    {
        var talk = GameUi.Get("Talk");
        if (talk == null) return false;
        GameUi.ClickTalk(talk);
        return true;
    }

    private void TickSelect()
    {
        if (autoRetainer.IsBusy) { Fail("AutoRetainer started: stopped so the two don't collide."); return; }
        if (ClickThroughTalk()) return;
        var list = GameUi.Get("RetainerList");
        if (list == null) return; // still on its way back

        if (queue.Count == 0)
        {
            // A second pass for anything a full retainer bounced to another one already passed.
            if (!scanOnly && pass < 2 && planner.RetainersToVisit() is { Count: > 0 } again)
            {
                pass++;
                foreach (var id in again) queue.Enqueue(id);
            }
            else { Finish(); return; }
        }

        current = queue.Dequeue();
        var me = tracker.Me();
        var index = me?.Order.IndexOf(current) ?? -1;
        if (index < 0) { phaseStart = DateTime.UtcNow; return; } // gone from the list; next one
        Status = $"{(scanOnly ? "Scanning" : "Stashing")}: {tracker.NameOf(current)}...";
        GameUi.Fire(list, 2, index);
        Set(Phase.OpenMenu);
        next = DateTime.UtcNow.AddSeconds(0.8);
    }

    private void TickOpenMenu()
    {
        if (ClickThroughTalk()) return;
        var ss = GameUi.Get("SelectString");
        if (ss == null) return;
        if (Tracker.ActiveRetainer() is not { } a || a.Id != current)
        {
            Fail($"Opened the wrong retainer (wanted {tracker.NameOf(current)}): the list order may have changed. Stopped.", "RetainerList");
            return;
        }
        var rows = GameUi.MenuEntries(ss, false);
        var row = rows.FindIndex(r => r.Contains("Entrust or withdraw items", StringComparison.OrdinalIgnoreCase));
        if (row < 0) { Fail($"No 'Entrust or withdraw items' in the retainer menu: {string.Join(" | ", rows)}", "SelectString"); return; }
        ss->FireCallbackInt(row);
        Set(Phase.OpenInventory);
    }

    private void TickOpenInventory()
    {
        if (!Tracker.RetainerInventoryOpen || !Tracker.Loaded(Tracker.RetainerPages)) return;
        tracker.RecordOpen();
        visited.Add(current);
        skipStacks.Clear();
        skipCrystals.Clear(); // per retainer: a crystal tried here is re-checked at the next one
        lastMove = null;
        Set(Phase.Work);
    }

    private void TickWork()
    {
        if (!Tracker.RetainerInventoryOpen) { Fail("The retainer's inventory closed."); return; }
        if (crystal != null) { TickCrystal(); return; }
        tracker.RecordOpen();
        if (scanOnly) { Set(Phase.CloseInventory); return; }
        if (DepositStep() || CrystalStep()) { phaseStart = DateTime.UtcNow; return; }
        Set(Phase.CloseInventory);
    }

    private void TickCloseInventory()
    {
        tracker.RecordOpen();
        GameUi.Close("ContextMenu");
        foreach (var name in new[] { "InventoryRetainerLarge", "InventoryRetainer" })
        {
            var inv = GameUi.Get(name);
            if (inv != null) { inv->Close(true); return; }
        }
        Set(Phase.Quit);
    }

    private void TickQuit()
    {
        if (GameUi.IsOpen("RetainerList")) { Set(Phase.Select); return; }
        if (ClickThroughTalk()) return;
        var ss = GameUi.Get("SelectString");
        if (ss == null) return;
        var rows = GameUi.MenuEntries(ss, false);
        var row = rows.FindIndex(r => r.StartsWith("Quit", StringComparison.OrdinalIgnoreCase));
        if (row < 0) { Fail($"No 'Quit' in the retainer menu: {string.Join(" | ", rows)}", "SelectString"); return; }
        ss->FireCallbackInt(row);
        Set(Phase.BackToList);
    }

    private void TickBackToList()
    {
        if (ClickThroughTalk()) return;
        if (GameUi.IsOpen("RetainerList")) Set(Phase.Select);
    }

    // ------------------------------------------------------------------ materials

    /// <summary>Move one stack from your bags to this retainer. True if something moved.</summary>
    private bool DepositStep()
    {
        var im = InventoryManager.Instance();
        if (im == null) return false;
        foreach (var s in Tracker.Stacks(Tracker.Bags, id => Items.Stashable(id, config)))
        {
            if (skipStacks.Contains((s.Type, s.Slot))) continue;
            if (planner.Best(s.ItemId) != current) continue;
            if (FindSlot(s.ItemId) is not { } dst) continue;

            // The same stack again with nothing changed: the game isn't taking it. Leave it.
            if (lastMove is { } lm && lm.Type == s.Type && lm.Slot == s.Slot && lm.Item == s.ItemId && lm.Qty == s.Qty)
            {
                if (++sameMoveCount >= 3) { skipStacks.Add((s.Type, s.Slot)); Plugin.Log.Warning($"Stash: {Items.Info(s.ItemId)?.Name} wouldn't move; skipped."); continue; }
            }
            else sameMoveCount = 0;
            lastMove = (s.Type, s.Slot, s.ItemId, s.Qty);

            im->MoveItemSlot(s.Type, (ushort)s.Slot, dst.Type, (ushort)dst.Slot, true);
            stacksMoved++;
            return true;
        }
        return false;
    }

    /// <summary>A stack of the same item (same quality) with room on the open retainer, else an empty slot (respecting the reserve).</summary>
    private (InventoryType Type, int Slot)? FindSlot(uint itemId)
    {
        var max = Tracker.StackMax(itemId);
        foreach (var s in Tracker.Stacks(Tracker.RetainerPages, id => id == itemId))
            if (s.Qty < max) return (s.Type, s.Slot);
        if (Tracker.UsedSlots(Tracker.RetainerPages) >= Tracker.RetainerSlots - config.ReserveSlots) return null;
        var im = InventoryManager.Instance();
        foreach (var t in Tracker.RetainerPages)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
                if (c->GetInventorySlot(i) is var slot && slot != null && slot->ItemId == 0) return (t, i);
        }
        return null;
    }

    // ------------------------------------------------------------------ crystals

    /// <summary>Start the next crystal move for this retainer. True if one started.</summary>
    private bool CrystalStep()
    {
        if (!config.IncludeCrystals || !Tracker.Loaded(new[] { InventoryType.RetainerCrystals })) return false;
        var mine = Tracker.Crystals(InventoryType.Crystals);
        var theirs = Tracker.Crystals(InventoryType.RetainerCrystals);
        for (var c = Items.FirstCrystal; c <= Items.LastCrystal; c++)
        {
            if (skipCrystals.Contains(c)) continue;
            var have = mine.TryGetValue(c, out var m) ? m.Qty : 0;
            var held = theirs.TryGetValue(c, out var t) ? t.Qty : 0;
            var keep = config.KeepOf(c);
            if (have > keep && planner.CrystalDepositTarget(c) == current)
            {
                var amount = Math.Min(have - keep, Items.CrystalCap - held);
                if (amount > 0) return StartCrystal(c, amount, true, have, held);
            }
            else if (have < keep && config.CrystalWithdrawToKeep && planner.CrystalWithdrawSource(c) == current)
            {
                var amount = Math.Min(Math.Min(keep - have, held), Items.CrystalCap - have);
                if (amount > 0) return StartCrystal(c, amount, false, have, held);
            }
        }
        return false;
    }

    private bool StartCrystal(uint id, int amount, bool deposit, int have, int held)
    {
        var im = InventoryManager.Instance();
        var slot = Tracker.CrystalSlot(id);
        // The whole stack: a plain move. Part of it: the item menu, which asks how many.
        if (deposit && amount == have)
        {
            im->MoveItemSlot(InventoryType.Crystals, (ushort)slot, InventoryType.RetainerCrystals, (ushort)slot, true);
            crystalsMoved += amount;
            skipCrystals.Add(id);
            return true;
        }
        if (!deposit && amount == held)
        {
            im->MoveItemSlot(InventoryType.RetainerCrystals, (ushort)slot, InventoryType.Crystals, (ushort)slot, true);
            crystalsMoved += amount;
            skipCrystals.Add(id);
            return true;
        }
        crystal = new CrystalOp { Id = id, Amount = amount, Deposit = deposit, Before = have };
        return true;
    }

    private void TickCrystal()
    {
        var op = crystal!;
        var now = Tracker.Crystals(InventoryType.Crystals).TryGetValue(op.Id, out var m) ? m.Qty : 0;
        var done = op.Deposit ? now <= op.Before - op.Amount : now >= op.Before + op.Amount;
        if (done || now != op.Before)
        {
            crystalsMoved += Math.Abs(now - op.Before);
            skipCrystals.Add(op.Id);
            crystal = null;
            phaseStart = DateTime.UtcNow;
            return;
        }

        var numeric = GameUi.Get("InputNumeric");
        if (numeric != null) { numeric->FireCallbackInt(op.Amount); next = DateTime.UtcNow.AddSeconds(0.6); return; }

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
                if (index >= 0) continue;
                if (op.Deposit ? t.Contains("Entrust", StringComparison.OrdinalIgnoreCase)
                               : t.Contains("Retrieve", StringComparison.OrdinalIgnoreCase) || t.Contains("Withdraw", StringComparison.OrdinalIgnoreCase))
                    index = i - 8;
            }
            if (index < 0)
            {
                GameUi.Dump("ContextMenu", $"crystal menu ({(op.Deposit ? "entrust" : "retrieve")})");
                menu->FireCallbackInt(-1);
                Plugin.ChatGui.PrintError($"[Stash] No {(op.Deposit ? "entrust" : "retrieve")} option in the crystal menu ({string.Join(" | ", entries)}); skipped that crystal. Dump saved.");
                skipCrystals.Add(op.Id);
                crystal = null;
                return;
            }
            GameUi.Fire(menu, 0, index, 0);
            next = DateTime.UtcNow.AddSeconds(0.7);
            return;
        }

        if (++op.Tries > 5)
        {
            Plugin.ChatGui.PrintError($"[Stash] Couldn't move {Items.Info(op.Id)?.Name}; skipped.");
            skipCrystals.Add(op.Id);
            crystal = null;
            return;
        }
        var slot = Tracker.CrystalSlot(op.Id);
        var owner = op.Deposit ? AddonId("InventoryLarge", "Inventory", "InventoryExpansion") : AddonId("InventoryRetainerLarge", "InventoryRetainer");
        AgentInventoryContext.Instance()->OpenForItemSlot(op.Deposit ? InventoryType.Crystals : InventoryType.RetainerCrystals, slot, 0, owner);
        next = DateTime.UtcNow.AddSeconds(0.7);
    }

    private static uint AddonId(params string[] names)
    {
        foreach (var n in names)
        {
            var a = GameUi.Get(n);
            if (a != null) return a->Id;
        }
        return 0;
    }
}
