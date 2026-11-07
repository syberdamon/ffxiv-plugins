using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MaterialStash;

/// <summary>
/// Moves stashable materials between your bags and the saddlebag (it has to be open), one stack at
/// a time: onto a stack of the same item with room first, else a free slot.
/// </summary>
public sealed unsafe class SaddleMover : IDisposable
{
    public enum Direction { None, ToSaddlebag, ToBags }

    private readonly Configuration config;
    private Direction dir = Direction.None;
    private DateTime next = DateTime.MinValue;
    private int moved;
    private (InventoryType Type, int Slot, uint Item, int Qty)? lastMove;
    private int sameCount;
    private readonly HashSet<(InventoryType, int)> skip = new();

    public string Status { get; private set; } = "";
    public bool Busy => dir != Direction.None;

    public SaddleMover(Configuration config)
    {
        this.config = config;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    /// <summary>Stacks that would move in that direction.</summary>
    public int Pending(Direction d) =>
        Tracker.Stacks(d == Direction.ToSaddlebag ? Tracker.Bags : Tracker.Saddlebag, id => Items.Stashable(id, config)).Count;

    public void Start(Direction d)
    {
        if (Busy) return;
        if (!Tracker.SaddlebagOpen) { Status = "Open your saddlebag first."; return; }
        dir = d;
        moved = 0;
        lastMove = null;
        skip.Clear();
        next = DateTime.UtcNow;
        Status = d == Direction.ToSaddlebag ? "Mats -> saddlebag..." : "Mats -> bags...";
    }

    public void Stop()
    {
        if (!Busy) return;
        dir = Direction.None;
        Status = $"Stopped after {moved} stack(s).";
    }

    private void Finish(string why)
    {
        var what = dir == Direction.ToSaddlebag ? "Mats -> saddlebag" : "Mats -> bags";
        dir = Direction.None;
        Status = $"{what}: {moved} stack(s), {why}.";
        Plugin.ChatGui.Print($"[Stash] {Status}");
    }

    private void OnUpdate(IFramework framework)
    {
        if (dir == Direction.None || DateTime.UtcNow < next) return;
        next = DateTime.UtcNow.AddSeconds(config.StepDelay);
        try
        {
            if (!Tracker.SaddlebagOpen) { Finish("saddlebag closed"); return; }
            var (from, to) = dir == Direction.ToSaddlebag ? (Tracker.Bags, Tracker.Saddlebag) : (Tracker.Saddlebag, Tracker.Bags);
            var im = InventoryManager.Instance();
            foreach (var s in Tracker.Stacks(from, id => Items.Stashable(id, config)))
            {
                if (skip.Contains((s.Type, s.Slot))) continue;
                if (FindSlot(to, s.ItemId) is not { } dst) { Finish("no room left"); return; }
                if (lastMove is { } lm && lm.Type == s.Type && lm.Slot == s.Slot && lm.Item == s.ItemId && lm.Qty == s.Qty)
                {
                    if (++sameCount >= 3) { skip.Add((s.Type, s.Slot)); continue; }
                }
                else sameCount = 0;
                lastMove = (s.Type, s.Slot, s.ItemId, s.Qty);
                im->MoveItemSlot(s.Type, (ushort)s.Slot, dst.Type, (ushort)dst.Slot, true);
                moved++;
                return;
            }
            Finish("done");
        }
        catch (Exception ex)
        {
            dir = Direction.None;
            Status = "Error (see /xllog)";
            Plugin.Log.Error(ex, "Saddlebag move failed");
        }
    }

    private static (InventoryType Type, int Slot)? FindSlot(InventoryType[] to, uint itemId)
    {
        var max = Tracker.StackMax(itemId);
        foreach (var s in Tracker.Stacks(to, id => id == itemId))
            if (s.Qty < max) return (s.Type, s.Slot);
        var im = InventoryManager.Instance();
        foreach (var t in to)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
                if (c->GetInventorySlot(i) is var slot && slot != null && slot->ItemId == 0) return (t, i);
        }
        return null;
    }
}
