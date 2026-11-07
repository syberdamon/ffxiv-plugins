using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using MateriaManager.Core;

namespace MateriaManager;

/// <summary>
/// Moves materia between your bags, the saddlebag and the materia retainer, one step at a time
/// (a short pause between steps, like doing it by hand). Exact amounts use the game's own stack
/// split: split off what's needed, then move that stack.
///  - Saddlebag opens: bag materia goes in (unless materia is being held in your bags for transmuting).
///  - "Pull all from saddlebag": everything comes into your bags and is held there.
///  - Materia retainer opens: each materia below its minimum is topped up to its maximum (from
///    your bags, then the saddlebag if open); anything above the maximum comes back to your bags.
///    What's left in your bags stays there for transmuting.
/// </summary>
public sealed unsafe class Storage : IDisposable
{
    private enum Mode { None, StoreSaddlebag, PullSaddlebag, Balance, PullGrades }

    private readonly Configuration config;
    private DateTime nextStep = DateTime.MinValue;
    private bool saddleWasOpen, retainerWasOpen;
    private Mode mode = Mode.None;
    private int steps;
    private (uint ItemId, int Qty, InventoryType[] From, InventoryType[] To)? pendingSplit;

    /// <summary>Materia in your bags is being kept for transmuting: don't sweep it into the saddlebag.</summary>
    public bool Holding { get; private set; }
    public string Status { get; private set; } = "";
    public bool Busy => mode != Mode.None;

    public Storage(Configuration config)
    {
        this.config = config;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    public static bool SaddlebagOpen => GameUi.IsOpen("InventoryBuddy") || GameUi.IsOpen("InventoryBuddy2");
    public static bool RetainerOpen => GameUi.IsOpen("InventoryRetainer") || GameUi.IsOpen("InventoryRetainerLarge");

    /// <summary>The retainer currently open (id, name), if any.</summary>
    public static (ulong Id, string Name)? ActiveRetainer()
    {
        var rm = RetainerManager.Instance();
        var r = rm == null ? null : rm->GetActiveRetainer();
        return r == null || r->RetainerId == 0 ? null : (r->RetainerId, MemoryHelper.ReadStringNullTerminated((nint)r + 8));
    }

    /// <summary>All your retainers (for picking the materia retainer).</summary>
    public static List<(ulong Id, string Name)> Retainers()
    {
        var list = new List<(ulong, string)>();
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) return list;
        for (uint i = 0; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r != null && r->RetainerId != 0) list.Add((r->RetainerId, MemoryHelper.ReadStringNullTerminated((nint)r + 8)));
        }
        return list;
    }

    public bool MateriaRetainerOpen =>
        RetainerOpen && config.RetainerId != 0 && ActiveRetainer() is { } a && a.Id == config.RetainerId;

    // ---- buttons
    public void PullAllFromSaddlebag()
    {
        if (!SaddlebagOpen) { Status = "Open your saddlebag first."; return; }
        Holding = true;
        Begin(Mode.PullSaddlebag);
    }

    public void StoreInSaddlebagNow()
    {
        if (!SaddlebagOpen) { Status = "Open your saddlebag first."; return; }
        Holding = false;
        Begin(Mode.StoreSaddlebag);
    }

    public void BalanceNow()
    {
        if (config.RetainerId == 0) { Status = "Pick your materia retainer first."; return; }
        if (!MateriaRetainerOpen) { Status = $"Open {config.RetainerName}'s inventory first."; return; }
        Begin(Mode.Balance);
    }

    /// <summary>Pull materia of these grades into your bags from the saddlebag and/or the open retainer.</summary>
    public void PullGrades(int from, int to)
    {
        if (from > to) (from, to) = (to, from);
        var src = Sources();
        if (src.Length == 0) { Status = "Open your saddlebag or a retainer's inventory first (and tick it as a source)."; return; }
        pullFrom = from; pullTo = to;
        Holding = true;
        Begin(Mode.PullGrades);
    }

    private int pullFrom = 1, pullTo = 12;

    private InventoryType[] Sources()
    {
        var list = new List<InventoryType>();
        if (config.PullFromSaddlebag && SaddlebagOpen) list.AddRange(MateriaData.Saddlebag);
        if (config.PullFromRetainer && RetainerOpen) list.AddRange(MateriaData.Retainer);
        return list.ToArray();
    }

    public void Release() { Holding = false; Status = "Bag materia released: the saddlebag will take it next time."; }
    public void Stop() { mode = Mode.None; pendingSplit = null; Status = "Stopped."; }

    private void Begin(Mode m) { mode = m; steps = 0; pendingSplit = null; nextStep = DateTime.UtcNow; }

    // ---- loop
    private void OnUpdate(IFramework framework)
    {
        try
        {
            var saddle = SaddlebagOpen;
            var retainer = RetainerOpen;
            if (mode == Mode.None)
            {
                if (retainer && !retainerWasOpen && config.AutoBalanceRetainer && MateriaRetainerOpen) Begin(Mode.Balance);
                else if (saddle && !saddleWasOpen && config.AutoStoreSaddlebag && !Holding && !retainer) Begin(Mode.StoreSaddlebag);
            }
            saddleWasOpen = saddle;
            retainerWasOpen = retainer;
            if (Holding && MateriaData.Count(MateriaData.Bags).Count == 0) Holding = false; // nothing left to hold

            if (mode == Mode.None || DateTime.UtcNow < nextStep) return;
            nextStep = DateTime.UtcNow.AddMilliseconds(400);

            var open = mode switch
            {
                Mode.StoreSaddlebag or Mode.PullSaddlebag => saddle,
                Mode.Balance => MateriaRetainerOpen,
                Mode.PullGrades => saddle || retainer,
                _ => false,
            };
            if (!open) { Finish($"window closed after {steps} step(s)"); return; }

            var result = mode switch
            {
                Mode.StoreSaddlebag => MoveAnyStack(MateriaData.Bags, MateriaData.Saddlebag),
                Mode.PullSaddlebag => MoveAnyStack(MateriaData.Saddlebag, MateriaData.Bags, id => config.PullKind(MateriaData.All()[id].Kind)),
                Mode.PullGrades => MoveAnyStack(Sources(), MateriaData.Bags, id => MateriaData.All()[id] is var m && m.Grade >= pullFrom && m.Grade <= pullTo && config.PullKind(m.Kind)),
                _ => BalanceStep(),
            };
            if (result == null) { steps++; Status = $"{Describe()}: {steps} step(s)..."; return; }
            Finish(result);
        }
        catch (Exception ex)
        {
            mode = Mode.None;
            Status = "Error (see /xllog)";
            Plugin.Log.Error(ex, "Materia storage failed");
        }
    }

    private string Describe() => mode switch
    {
        Mode.StoreSaddlebag => "Bags -> saddlebag",
        Mode.PullSaddlebag => "Saddlebag -> bags",
        Mode.Balance => $"Balancing {config.RetainerName}",
        Mode.PullGrades => $"Pulling grade {MateriaData.Roman(pullFrom)}-{MateriaData.Roman(pullTo)} into bags",
        _ => "",
    };

    private void Finish(string how)
    {
        var what = Describe();
        if (mode == Mode.Balance) RecordRetainer();
        mode = Mode.None;
        pendingSplit = null;
        Status = $"{what}: {how}.";
        if (steps > 0 || how != "done") Plugin.ChatGui.Print($"[Materia] {Status}");
        // Whatever stayed in your bags after a retainer visit is for transmuting.
        if (what.StartsWith("Balancing") && MateriaData.Count(MateriaData.Bags).Count > 0) Holding = true;
    }

    private void RecordRetainer()
    {
        config.RetainerSeen = MateriaData.Count(MateriaData.Retainer);
        config.RetainerSeenAt = DateTime.Now;
        config.Save();
    }

    /// <summary>One whole stack of any materia from -> to. null = moved (keep going), else the end reason.</summary>
    private string? MoveAnyStack(InventoryType[] from, InventoryType[] to, Func<uint, bool>? which = null)
    {
        var im = InventoryManager.Instance();
        foreach (var (type, slot, item, qty) in Stacks(im, from))
        {
            if (which != null && !which(item)) continue;
            if (FindSlot(im, to, item) is not { } dst) return "no room left";
            im->MoveItemSlot(type, (ushort)slot, dst.Type, (ushort)dst.Slot, true);
            return null;
        }
        return "done";
    }

    /// <summary>One step toward every retainer target. null = did something, else the end reason.</summary>
    private string? BalanceStep()
    {
        var im = InventoryManager.Instance();

        // Finish a split from last step: move the stack we split off.
        if (pendingSplit is { } ps)
        {
            pendingSplit = null;
            var split = Stacks(im, ps.From).FirstOrDefault(s => s.Item == ps.ItemId && s.Qty == ps.Qty);
            if (split.Item != 0 && FindSlot(im, ps.To, ps.ItemId) is { } d)
            {
                im->MoveItemSlot(split.Type, (ushort)split.Slot, d.Type, (ushort)d.Slot, true);
                return null;
            }
            return "a split stack didn't appear (no free slot?)";
        }

        var all = MateriaData.All();
        var inRetainer = MateriaData.Count(MateriaData.Retainer);
        var sources = config.FillFromSaddlebag && SaddlebagOpen ? MateriaData.Bags.Concat(MateriaData.Saddlebag).ToArray() : MateriaData.Bags;
        var available = MateriaData.Count(sources);

        foreach (var m in all.Values.OrderBy(x => x.Type).ThenByDescending(x => x.Grade))
        {
            if (config.TargetFor(m) is not { } t) continue;
            var have = inRetainer.GetValueOrDefault(m.ItemId);
            if (have > t.Max)
                return Transfer(im, m.ItemId, have - t.Max, MateriaData.Retainer, MateriaData.Bags);
            if (have < t.Min && available.GetValueOrDefault(m.ItemId) > 0)
                return Transfer(im, m.ItemId, Math.Min(t.Max - have, available[m.ItemId]), sources, MateriaData.Retainer);
        }
        return "done";
    }

    /// <summary>Move exactly qty of an item: a whole stack if it fits, else split first (next step moves it).</summary>
    private string? Transfer(InventoryManager* im, uint itemId, int qty, InventoryType[] from, InventoryType[] to)
    {
        var stack = Stacks(im, from).Where(s => s.Item == itemId).OrderBy(s => s.Qty).ToList();
        if (stack.Count == 0) return "done";
        if (FindSlot(im, to, itemId) is not { } dst) return "no room left";
        var exact = stack.FirstOrDefault(s => s.Qty <= qty);
        if (exact.Item != 0)
        {
            im->MoveItemSlot(exact.Type, (ushort)exact.Slot, dst.Type, (ushort)dst.Slot, true);
            return null;
        }
        var big = stack[0];   // smallest stack that's still bigger than needed
        im->SplitItem(big.Type, (ushort)big.Slot, qty);
        pendingSplit = (itemId, qty, from, to);
        nextStep = DateTime.UtcNow.AddMilliseconds(700);
        return null;
    }

    private static IEnumerable<(InventoryType Type, int Slot, uint Item, int Qty)> Stacks(InventoryManager* im, InventoryType[] containers)
    {
        var result = new List<(InventoryType, int, uint, int)>();
        if (im == null) return result;
        foreach (var t in containers)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s != null && s->ItemId != 0 && MateriaData.IsMateria(s->ItemId)) result.Add((t, i, s->ItemId, (int)s->Quantity));
            }
        }
        return result;
    }

    /// <summary>A stack of the same item with room, else an empty slot.</summary>
    private static (InventoryType Type, int Slot)? FindSlot(InventoryManager* im, InventoryType[] to, uint itemId)
    {
        var stackMax = (int)(Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.StackSize ?? 999);
        (InventoryType, int)? empty = null;
        foreach (var t in to)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null) continue;
                if (s->ItemId == itemId && s->Quantity < stackMax) return (t, i);
                if (s->ItemId == 0 && empty == null) empty = (t, i);
            }
        }
        return empty;
    }
}
